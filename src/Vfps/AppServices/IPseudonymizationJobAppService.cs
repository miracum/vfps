using System.Security.Claims;
using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <summary>
/// CSV pseudonymization job operations shared by Blazor Server components and the presigned
/// upload/download flow. Job input/output bytes never pass through this service or Kestrel -
/// see <see cref="CsvProcessing.CsvPseudonymizationJobRunner"/> for the actual S3-to-S3 pipeline.
/// </summary>
public interface IPseudonymizationJobAppService
{
    /// <summary>
    /// Creates a job record (status <see cref="PseudonymizationJobStatus.AwaitingUpload"/>) and
    /// returns a presigned S3 PUT URL for the caller to upload the input file to directly.
    /// Requires write access (Pseudonymize) or reverse-lookup access (Depseudonymize) - see
    /// <see cref="CreateCsvJobRequest.Direction"/> - to every namespace referenced in
    /// <paramref name="request"/>'s column mappings.
    /// </summary>
    Task<(PseudonymizationJob Job, string UploadUrl)> CreateJobAsync(
        CreateCsvJobRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Called once the caller's browser-to-S3 PUT resolves. Verifies the object actually exists
    /// (a HEAD request - the PUT response alone isn't trusted) before transitioning the job to
    /// Queued and enqueueing the Hangfire processing job.
    /// </summary>
    Task MarkUploadCompleteAsync(
        Guid jobId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>Requires the caller to be the job's creator, or an admin.</summary>
    Task<PseudonymizationJob> GetAsync(
        Guid jobId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>Lists the caller's own jobs, or every job for admins.</summary>
    Task<IReadOnlyList<PseudonymizationJob>> ListAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Best-effort cancellation: removes the job from Hangfire's queue if it hasn't started yet,
    /// and flips a status flag the running job runner cooperatively checks between rows. Requires
    /// the caller to be the job's creator, or an admin.
    /// </summary>
    Task CancelAsync(Guid jobId, ClaimsPrincipal user, CancellationToken cancellationToken);

    /// <summary>Presigned S3 GET URL for a completed job's output file.</summary>
    Task<string> GetDownloadUrlAsync(
        Guid jobId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes every job of the caller's own in a terminal state (Completed, Failed, Cancelled),
    /// or every such job for admins - matching <see cref="ListAsync"/>'s own scoping. Their input
    /// and output objects in S3 are left alone; the bucket's own retention/lifecycle rule (see
    /// <see cref="CsvProcessing.S3BucketConfigurationBackgroundService"/>) expires those
    /// independently of whether a DB row still references them. Returns the number of jobs
    /// deleted.
    /// </summary>
    Task<int> ClearFinishedAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}

public record CreateCsvJobRequest(
    string Encoding,
    string Delimiter,
    bool HasHeaderRow,
    IReadOnlyList<ColumnMapping> ColumnMappings,
    PseudonymizationJobDirection Direction = PseudonymizationJobDirection.Pseudonymize,
    string? OriginalFileName = null
);

public class PseudonymizationJobNotFoundException(Guid jobId)
    : Exception($"The requested pseudonymization job '{jobId}' does not exist.")
{
    public Guid JobId { get; } = jobId;
}
