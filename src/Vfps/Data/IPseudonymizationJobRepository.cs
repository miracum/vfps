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
    /// Bulk-updates progress counters without loading/tracking the entity - called frequently
    /// from the job runner, so avoiding EF change-tracking overhead matters here.
    /// </summary>
    Task UpdateProgressAsync(
        Guid id,
        long bytesProcessed,
        long rowsProcessed,
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
}
