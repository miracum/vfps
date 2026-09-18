using Vfps.Data.Models;

namespace Vfps.Data;

/// <summary>
/// Store and retrieve CSV pseudonymization jobs.
/// </summary>
public interface IPseudonymizationJobRepository
{
    Task<PseudonymizationJob> CreateAsync(
        PseudonymizationJob job,
        CancellationToken cancellationToken
    );

    Task<PseudonymizationJob?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Lists jobs created by <paramref name="createdBy"/>, or every job when
    /// <paramref name="createdBy"/> is null (admin callers see all jobs - see
    /// <see cref="AppServices.IPseudonymizationJobAppService"/>).
    /// </summary>
    Task<IReadOnlyList<PseudonymizationJob>> ListAsync(
        string? createdBy,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// IDs of jobs stuck in <see cref="PseudonymizationJobStatus.Running"/> with no progress
    /// update in over <paramref name="staleAfter"/> - see
    /// <see cref="CsvProcessing.StalledPseudonymizationJobWatchdogService"/>, the only caller.
    /// A healthy running job's LastUpdatedAt moves at least every ~2s/200 rows (see
    /// CsvPseudonymizationJobRunner's ProgressUpdateInterval/ProgressUpdateRowInterval), so this
    /// only ever finds jobs whose runner crashed, was killed, or exhausted every retry against an
    /// extended database outage without ever getting to record its own failure.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindStalledRunningJobIdsAsync(
        TimeSpan staleAfter,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Bulk-updates progress counters without loading/tracking the entity - called frequently
    /// from the job runner, so avoiding EF change-tracking overhead matters here.
    /// </summary>
    Task UpdateProgressAsync(
        Guid id,
        long bytesProcessed,
        long rowsProcessed,
        int badDataRowCount,
        int missingValueCount,
        int unresolvedValueCount,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// <see cref="UpdateProgressAsync"/>, plus the "has this job been cancelled" answer the
    /// runner needs on the very same cadence - in one round trip rather than an update followed
    /// by a separate read.
    ///
    /// The two fold together because the answer is already implicit in the update: the statement
    /// simply declines to match a cancelled job, so a row count of zero *is* the cancellation
    /// signal and no status has to be read back. That halves the database round trips spent on
    /// progress bookkeeping, which is the part of a CSV job whose cost scales with round-trip
    /// latency rather than with rows - on a pseudonymize job at the default batch size it was
    /// issuing ten round trips per thousand rows against the one the actual upsert costs.
    /// </summary>
    /// <returns>
    /// false if the job has been cancelled (or no longer exists) and processing should stop, true
    /// if it should continue.
    /// </returns>
    Task<bool> UpdateProgressUnlessCancelledAsync(
        Guid id,
        long bytesProcessed,
        long rowsProcessed,
        int badDataRowCount,
        int missingValueCount,
        int unresolvedValueCount,
        CancellationToken cancellationToken
    );

    Task UpdateStatusAsync(
        Guid id,
        PseudonymizationJobStatus status,
        string? errorMessage,
        CancellationToken cancellationToken
    );

    /// <summary>Transitions a job from AwaitingUpload to Queued once its input file is confirmed present.</summary>
    Task MarkQueuedAsync(Guid id, long totalBytes, CancellationToken cancellationToken);

    Task SetHangfireJobIdAsync(Guid id, string hangfireJobId, CancellationToken cancellationToken);

    Task CompleteAsync(
        Guid id,
        string outputObjectKey,
        long rowsProcessed,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes every job in a terminal state (Completed, Failed, Cancelled, Stalled) - Running/Queued/
    /// AwaitingUpload jobs are never touched, so an in-progress job can't be deleted out from
    /// under its own runner. Scoped to <paramref name="createdBy"/>, or every such job when null
    /// (admin callers - see <see cref="AppServices.IPseudonymizationJobAppService"/>). Returns the
    /// number of jobs deleted.
    /// </summary>
    Task<int> DeleteFinishedAsync(string? createdBy, CancellationToken cancellationToken);
}
