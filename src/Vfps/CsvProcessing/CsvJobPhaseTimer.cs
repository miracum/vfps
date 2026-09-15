using System.Diagnostics;
using System.Diagnostics.Metrics;
using Vfps.Data.Models;

namespace Vfps.CsvProcessing;

/// <summary>
/// The four things a CSV job spends its wall clock on. Deliberately coarse: the question this
/// exists to answer is "is this job bound by object storage, by the database, or by neither",
/// which no amount of finer detail makes easier to read off a dashboard.
/// </summary>
internal enum CsvJobPhase
{
    /// <summary>
    /// Pulling the input file's bytes and parsing them into fields. Object-storage bound - the
    /// CSV parser only blocks because the underlying S3 response stream hasn't delivered yet.
    /// Always zero for <see cref="PseudonymizationJobDirection.Export"/>, which has no input file.
    /// </summary>
    ReadInput,

    /// <summary>
    /// Resolving a chunk against the database - the batched upsert, the concurrent reverse
    /// lookups, the import batch, or the export's page read, depending on direction.
    /// </summary>
    ResolveDatabase,

    /// <summary>
    /// Writing resolved rows out. Object-storage bound in the same indirect way as
    /// <see cref="ReadInput"/>: the writes go into a pipe that <see cref="CsvJobOutputUploader"/>
    /// uploads from concurrently, so this only blocks once that pipe fills, which means the
    /// upload is not keeping up.
    /// </summary>
    WriteOutput,

    /// <summary>
    /// Persisting progress and noticing a cancellation - see
    /// <see cref="CsvJobProgressReporter.MaybeReportAndCheckCancelledAsync"/>. Broken out from
    /// <see cref="ResolveDatabase"/> rather than folded into it precisely because it is *not*
    /// the job's actual work: its cost scales with round-trip latency and the check-in cadence
    /// rather than with rows, so a deployment where it grows into a visible share of the total
    /// is one whose cadence wants revisiting.
    /// </summary>
    ReportProgress,
}

/// <summary>
/// Accumulates one CSV job's time per <see cref="CsvJobPhase"/> and reports the totals once, when
/// the job ends.
///
/// Totals rather than a per-occurrence histogram: the question is what share of a job went where,
/// and a counter of seconds answers that directly (<c>sum by (phase) (rate(...))</c>) where a
/// distribution of several-million individual chunk timings does not. Reported at the end rather
/// than as it goes so a job contributes one set of measurements instead of one per chunk.
/// </summary>
/// <param name="direction">Tags every measurement, since the phase mix differs sharply per direction.</param>
internal sealed class CsvJobPhaseTimer(PseudonymizationJobDirection direction)
{
    private static readonly Counter<double> PhaseDuration = Program.Meter.CreateCounter<double>(
        "vfps.csv.job.phase.duration.seconds",
        unit: "s",
        description: "Total time CSV pseudonymization jobs spent in each phase of processing, by job direction."
    );

    // Indexed by (int)CsvJobPhase. Plain += with no synchronization: a job's phases are measured
    // from its own single, linear async flow - even the depseudonymize path, whose concurrency
    // lives entirely inside one ResolveDatabase scope rather than across several - so these are
    // only ever written sequentially, with each await establishing the ordering between them.
    private readonly double[] elapsedSeconds = new double[Enum.GetValues<CsvJobPhase>().Length];

    /// <summary>
    /// Times everything up to the returned scope's disposal as <paramref name="phase"/>. Scopes
    /// are not nested or overlapped anywhere - each one covers a distinct stretch of the job - so
    /// the four totals sum to (very nearly) the job's own duration and can be read as shares of it.
    /// </summary>
    public Scope Measure(CsvJobPhase phase) => new(this, phase);

    /// <summary>
    /// Publishes the totals as counter increments, and mirrors them onto <paramref name="activity"/>
    /// so a single job's trace carries its own breakdown without needing the metrics backend
    /// alongside it. Called once per job, including for a failed or cancelled one - a job that
    /// died is exactly when the breakdown is most worth having.
    /// </summary>
    public void Flush(Activity? activity)
    {
        foreach (var phase in Enum.GetValues<CsvJobPhase>())
        {
            var seconds = elapsedSeconds[(int)phase];
            var phaseTag = TagValue(phase);

            // Recorded even when zero, so every phase/direction series exists from the first job
            // onward and a stacked dashboard doesn't silently omit the phase that happens not to
            // apply to the direction being looked at.
            PhaseDuration.Add(
                seconds,
                new KeyValuePair<string, object?>("phase", phaseTag),
                new KeyValuePair<string, object?>("direction", direction.ToString())
            );

            activity?.SetTag($"vfps.csv.phase.{phaseTag}.seconds", seconds);
        }
    }

    private void Add(CsvJobPhase phase, double seconds) => elapsedSeconds[(int)phase] += seconds;

    // Snake_case rather than the enum's own PascalCase: this is a metric label value, and every
    // other name this app exports is snake_case by the time Prometheus sees it.
    private static string TagValue(CsvJobPhase phase) =>
        phase switch
        {
            CsvJobPhase.ReadInput => "read_input",
            CsvJobPhase.ResolveDatabase => "resolve_database",
            CsvJobPhase.WriteOutput => "write_output",
            CsvJobPhase.ReportProgress => "report_progress",
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };

    /// <summary>
    /// Returned by <see cref="Measure"/>; adds its lifetime to that phase's total on disposal.
    /// A plain struct rather than a ref struct because every one of these spans at least one
    /// await, which a ref struct cannot.
    /// </summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly CsvJobPhaseTimer owner;
        private readonly CsvJobPhase phase;
        private readonly long startedAt;

        internal Scope(CsvJobPhaseTimer owner, CsvJobPhase phase)
        {
            this.owner = owner;
            this.phase = phase;
            startedAt = Stopwatch.GetTimestamp();
        }

        public void Dispose() => owner.Add(phase, Stopwatch.GetElapsedTime(startedAt).TotalSeconds);
    }
}
