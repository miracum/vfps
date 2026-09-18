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
    /// Creates an already-queued <see cref="PseudonymizationJobDirection.Export"/> job for
    /// <paramref name="request"/>'s namespace and enqueues it immediately - unlike every other
    /// direction, there is no input file to wait for, so it skips
    /// <see cref="PseudonymizationJobStatus.AwaitingUpload"/> and
    /// <see cref="MarkUploadCompleteAsync"/> entirely and no upload URL is issued. Requires
    /// reverse-lookup access to the namespace: the output is its original values, in bulk.
    /// </summary>
    Task<PseudonymizationJob> CreateExportJobAsync(
        CreateCsvExportJobRequest request,
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

    /// <summary>
    /// Requires the caller to be the job's creator, or an admin, and to still hold the access
    /// every namespace the job touches demands - re-checked here rather than trusted from when
    /// the job was created, since a grant can be revoked in between.
    /// </summary>
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

    /// <summary>
    /// Presigned S3 GET URL for a completed job's output file. Requires the caller to be the
    /// job's creator, or an admin, and to still hold the access the job's namespaces demand -
    /// for a de-pseudonymization job the output is the original values, and it outlives the job
    /// run by the bucket's retention period.
    /// </summary>
    Task<string> GetDownloadUrlAsync(
        Guid jobId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes every job of the caller's own in a terminal state (Completed, Failed, Cancelled,
    /// Stalled), or every such job for admins - matching <see cref="ListAsync"/>'s own scoping.
    /// Their input and output objects in S3 are left alone; the bucket's own retention/lifecycle
    /// rule (see <see cref="CsvProcessing.S3BucketConfigurationBackgroundService"/>) expires
    /// those independently of whether a DB row still references them. Returns the number of jobs
    /// deleted.
    /// </summary>
    Task<int> ClearFinishedAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}

/// <summary>
/// One CSV job to create, other than an export - see <see cref="CreateCsvExportJobRequest"/> for
/// why that one has a request of its own.
///
/// <paramref name="PseudonymizeMode"/> decides what becomes of an original value the namespace
/// holds no pseudonym for (see <see cref="Data.Models.PseudonymizeMode"/>). It is only valid to
/// set away from its default on a <see cref="PseudonymizationJobDirection.Pseudonymize"/> job;
/// every other direction rejects it rather than ignoring it, so a caller asking for a guarantee
/// that job cannot give is told so instead of quietly receiving the default behavior.
/// </summary>
public record CreateCsvJobRequest(
    string Encoding,
    string Delimiter,
    bool HasHeaderRow,
    IReadOnlyList<ColumnMapping> ColumnMappings,
    PseudonymizationJobDirection Direction = PseudonymizationJobDirection.Pseudonymize,
    string? OriginalFileName = null,
    PseudonymizeMode PseudonymizeMode = PseudonymizeMode.CreateIfMissing
);

/// <summary>
/// An export has no file to describe, so it takes the namespace and the output's formatting
/// directly rather than reusing <see cref="CreateCsvJobRequest"/> and leaving half of it
/// meaningless. <paramref name="OriginalValueColumn"/>/<paramref name="PseudonymValueColumn"/> name
/// the two columns written out, defaulting to what
/// <see cref="PseudonymizationJobDirection.Import"/> expects to read back in.
/// </summary>
public record CreateCsvExportJobRequest(
    string NamespaceName,
    string Encoding = "utf-8",
    string Delimiter = ",",
    string OriginalValueColumn = CsvNamespaceColumns.OriginalValue,
    string PseudonymValueColumn = CsvNamespaceColumns.PseudonymValue
);

/// <summary>
/// The column names a namespace import/export file uses by default. Shared by the export request
/// above and the admin UI's import form, so a file exported from one namespace imports into
/// another without the operator having to restate the column names on either side.
/// </summary>
public static class CsvNamespaceColumns
{
    public const string OriginalValue = "original";
    public const string PseudonymValue = "pseudonym";
}

public class PseudonymizationJobNotFoundException(Guid jobId)
    : Exception($"The requested pseudonymization job '{jobId}' does not exist.")
{
    public Guid JobId { get; } = jobId;
}
