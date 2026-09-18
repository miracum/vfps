using System.Diagnostics;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.CsvProcessing;

/// <summary>
/// One CSV job's progress bookkeeping: the throttled "check in with the database" cadence every
/// direction shares, plus the recovered-problem counters the UI surfaces alongside it.
///
/// Created per job by <see cref="CsvPseudonymizationJobRunner"/> and handed to whichever
/// <see cref="ICsvJobProcessor"/> runs it, so all four directions report progress and notice a
/// cancellation on the same schedule rather than each inventing its own.
/// </summary>
/// <param name="jobRepository">Where progress is persisted to and the job's status read back from.</param>
/// <param name="jobId">The job being reported on.</param>
/// <param name="phases">
/// Attributes this bookkeeping's own cost to <see cref="CsvJobPhase.ReportProgress"/>, separately
/// from the database work the job is actually there to do.
/// </param>
/// <param name="progressUpdateInterval">
/// Minimum wall time between check-ins, from
/// <see cref="Config.CsvProcessingConfig.ProgressUpdateInterval"/>. <see cref="TimeSpan.Zero"/>
/// makes every <see cref="ProgressUpdateRowInterval"/>th row a check-in, which is what the unit
/// tests want and what a production deployment very much does not.
/// </param>
internal sealed class CsvJobProgressReporter(
    IPseudonymizationJobRepository jobRepository,
    Guid jobId,
    CsvJobPhaseTimer phases,
    TimeSpan progressUpdateInterval
)
{
    /// <summary>
    /// How often the row counter is even looked at. Only a cheap modulo gate in front of the
    /// clock read below - it is <see cref="Config.CsvProcessingConfig.ProgressUpdateInterval"/>
    /// that decides whether an
    /// update actually happens, so this bounds how far past the interval a check-in can slip
    /// (one interval plus up to this many rows), not how many updates there are.
    /// </summary>
    private const int ProgressUpdateRowInterval = 200;

    /// <summary>
    /// Minimum wall time between two check-ins. Each one is a committing round trip, so this is
    /// what bounds their cost on a long job: a million-row import used to issue one every 200
    /// rows regardless of elapsed time - 5,000 commits, ~20/s at observed throughput - because
    /// the two gates below were combined with `&amp;&amp;`, which fires as soon as *either* opens.
    /// With both required, the count follows the job's duration instead of its row count.
    ///
    /// Deliberately well above the ~2s the admin UI's job grid polls at: the progress bar simply
    /// advances in larger steps, while a cross-AZ deployment - where a synchronous commit costs
    /// milliseconds rather than the sub-millisecond it costs when every instance shares a host -
    /// stops paying for a check-in per 200 rows. Also the ceiling on how long a cancel click
    /// takes to be noticed, which is why this is seconds rather than minutes, and it must stay
    /// far below <see cref="Config.CsvProcessingConfig.StalledJobThreshold"/> or a healthy job
    /// would be marked Stalled.
    ///
    /// Supplied per job from <see cref="Config.CsvProcessingConfig.ProgressUpdateInterval"/>.
    /// </summary>
    private readonly TimeSpan progressUpdateInterval = progressUpdateInterval;

    private readonly Stopwatch sinceLastUpdate = Stopwatch.StartNew();

    /// <summary>
    /// Reads the job's current bytes-consumed count at report time. A processor that reads an
    /// input file points this at its own <see cref="ByteCountingStream"/> as soon as it has one;
    /// <see cref="PseudonymizationJobDirection.Export"/> has no input file at all and leaves it at
    /// the constant 0 below, which the UI already renders as "rows only, no progress bar" (an
    /// export job's <see cref="PseudonymizationJob.TotalBytes"/> is 0 too, so there is nothing to
    /// show a fraction of).
    /// </summary>
    public Func<long> BytesProcessed { get; set; } = () => 0;

    /// <summary>
    /// Rows CsvHelper flagged as malformed but recovered from - incremented from inside the
    /// <c>BadDataFound</c> closure on the job's <see cref="CsvHelper.Configuration.CsvConfiguration"/>
    /// (see <see cref="CsvJobFormat.CreateConfiguration"/>), which is why this
    /// lives on a shared object rather than as a local the reporting code below couldn't observe.
    /// </summary>
    public int BadDataRowCount { get; set; }

    /// <summary>
    /// Values passed through/skipped rather than transformed because they were blank or a
    /// configured "missing data" placeholder - see
    /// <see cref="CsvJobFormat.IsMissingValue"/>. Same reason as
    /// <see cref="BadDataRowCount"/> for living here: it's incremented deep inside the per-chunk
    /// flush paths and observed by the reporting code below.
    /// </summary>
    public int MissingValueCount { get; set; }

    /// <summary>
    /// Fields blanked because the namespace held no pseudonym for them and the job was told not to
    /// create one - <see cref="PseudonymizeMode.BlankIfMissing"/> only. Lives here for the same
    /// reason as the two counters above: it is incremented deep inside the per-chunk flush path
    /// and observed by the reporting code below.
    /// </summary>
    public int UnresolvedValueCount { get; set; }

    /// <summary>
    /// Called on every row read - cheap to skip via the interval/elapsed-time gate below - rather
    /// than only on flush boundaries, which for a pseudonymize job can be up to
    /// <see cref="Config.CsvProcessingConfig.PseudonymizeBatchSize"/> rows apart. Tying this to
    /// that boundary would mean a cancel click, or the UI's progress bar, could lag by that many
    /// rows instead of the <see cref="Config.CsvProcessingConfig.ProgressUpdateInterval"/>
    /// cadence this aims for.
    /// </summary>
    /// <param name="totalRowsRead">
    /// Rows consumed from the source so far (flushed or still buffered) - gates *when* this checks
    /// in, independent of <paramref name="rowsWritten"/>.
    /// </param>
    /// <param name="rowsWritten">Rows actually flushed/written so far - what gets persisted.</param>
    /// <returns>true if the job was cancelled and processing should stop.</returns>
    public async Task<bool> MaybeReportAndCheckCancelledAsync(long totalRowsRead, long rowsWritten)
    {
        // Both gates must open, not either: the row gate is only here to keep the clock read off
        // the per-row path, and on its own it fired every 200 rows however fast the job ran.
        if (
            totalRowsRead % ProgressUpdateRowInterval != 0
            || sinceLastUpdate.Elapsed < progressUpdateInterval
        )
        {
            return false;
        }

        using var scope = phases.Measure(CsvJobPhase.ReportProgress);

        // One round trip for both halves - persisting progress and learning whether the job has
        // been cancelled - rather than an update followed by a read. See
        // IPseudonymizationJobRepository.UpdateProgressUnlessCancelledAsync for why the two fold
        // together, and why halving this particular cost is worth a dedicated method.
        var stillActive = await jobRepository.UpdateProgressUnlessCancelledAsync(
            jobId,
            BytesProcessed(),
            rowsWritten,
            BadDataRowCount,
            MissingValueCount,
            UnresolvedValueCount,
            CancellationToken.None
        );
        sinceLastUpdate.Restart();

        return !stillActive;
    }

    /// <summary>
    /// Unconditional check-in - used for the final report once the source is exhausted, where the
    /// gate above would otherwise drop the last partial interval's progress.
    /// </summary>
    public async Task ReportAsync(long rowsWritten)
    {
        using var scope = phases.Measure(CsvJobPhase.ReportProgress);

        await jobRepository.UpdateProgressAsync(
            jobId,
            BytesProcessed(),
            rowsWritten,
            BadDataRowCount,
            MissingValueCount,
            UnresolvedValueCount,
            CancellationToken.None
        );
        sinceLastUpdate.Restart();
    }
}
