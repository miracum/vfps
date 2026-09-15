using System.Diagnostics;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.CsvProcessing;

/// <summary>
/// One CSV job's progress bookkeeping: the throttled "check in with the database" cadence every
/// direction shares, plus the two recovered-problem counters the UI surfaces alongside it.
///
/// Created per job by <see cref="CsvPseudonymizationJobRunner"/> and handed to whichever
/// <see cref="ICsvJobProcessor"/> runs it, so all four directions report progress and notice a
/// cancellation on the same schedule rather than each inventing its own.
/// </summary>
/// <param name="jobRepository">Where progress is persisted to and the job's status read back from.</param>
/// <param name="jobId">The job being reported on.</param>
internal sealed class CsvJobProgressReporter(
    IPseudonymizationJobRepository jobRepository,
    Guid jobId
)
{
    private const int ProgressUpdateRowInterval = 200;
    private static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromSeconds(2);

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
    /// Called on every row read - cheap to skip via the interval/elapsed-time gate below - rather
    /// than only on flush boundaries, which for a pseudonymize job can be up to
    /// <see cref="Config.CsvProcessingConfig.PseudonymizeBatchSize"/> rows apart. Tying this to
    /// that boundary would mean a cancel click, or the UI's progress bar, could lag by that many
    /// rows instead of the ~<see cref="ProgressUpdateRowInterval"/>/2s cadence this aims for.
    /// </summary>
    /// <param name="totalRowsRead">
    /// Rows consumed from the source so far (flushed or still buffered) - gates *when* this checks
    /// in, independent of <paramref name="rowsWritten"/>.
    /// </param>
    /// <param name="rowsWritten">Rows actually flushed/written so far - what gets persisted.</param>
    /// <returns>true if the job was cancelled and processing should stop.</returns>
    public async Task<bool> MaybeReportAndCheckCancelledAsync(long totalRowsRead, long rowsWritten)
    {
        if (
            totalRowsRead % ProgressUpdateRowInterval != 0
            && sinceLastUpdate.Elapsed < ProgressUpdateInterval
        )
        {
            return false;
        }

        await ReportAsync(rowsWritten);

        var current = await jobRepository.FindAsync(jobId, CancellationToken.None);
        return current?.Status == PseudonymizationJobStatus.Cancelled;
    }

    /// <summary>
    /// Unconditional check-in - used for the final report once the source is exhausted, where the
    /// gate above would otherwise drop the last partial interval's progress.
    /// </summary>
    public async Task ReportAsync(long rowsWritten)
    {
        await jobRepository.UpdateProgressAsync(
            jobId,
            BytesProcessed(),
            rowsWritten,
            BadDataRowCount,
            MissingValueCount,
            CancellationToken.None
        );
        sinceLastUpdate.Restart();
    }
}
