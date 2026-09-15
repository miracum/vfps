using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Security.Claims;
using Amazon.S3;
using Amazon.S3.Model;
using Hangfire;
using Microsoft.Extensions.Options;
using Vfps.Authorization;
using Vfps.Config;
using Vfps.CsvProcessing;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <inheritdoc cref="IPseudonymizationJobAppService"/>
public class PseudonymizationJobAppService(
    IPseudonymizationJobRepository jobRepository,
    INamespacePermissionChecker permissionChecker,
    IAmazonS3 s3,
    IBackgroundJobClient backgroundJobClient,
    IOptions<S3Config> s3Config
) : IPseudonymizationJobAppService
{
    /// <summary>
    /// Shared by <see cref="CsvPseudonymizationJobRunner"/> (which writes the output object
    /// under this prefix) and <see cref="S3BucketConfigurationBackgroundService"/> (which scopes
    /// the bucket's expiration rule to it), so all three stay in sync on a single literal.
    /// </summary>
    public const string S3ObjectKeyPrefix = "csv-jobs/";

    private S3Config Config => s3Config.Value;

    // GetPreSignedUrlRequest.Protocol defaults to https regardless of the S3 client's own
    // ServiceURL/UseHttp configuration - without this, a plain-HTTP endpoint (e.g. local MinIO)
    // would still get its presigned URLs signed as "https://", which browsers then fail to load.
    private Protocol PresignedUrlProtocol =>
        Config.ServiceUrl.StartsWith("http://", StringComparison.Ordinal)
            ? Protocol.HTTP
            : Protocol.HTTPS;

    /// <inheritdoc/>
    public async Task<(PseudonymizationJob Job, string UploadUrl)> CreateJobAsync(
        CreateCsvJobRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        await EnsureNamespaceAccessAsync(
            request.ColumnMappings.Select(m => m.Namespace),
            request.Direction,
            user,
            cancellationToken
        );

        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var job = new PseudonymizationJob
        {
            Id = jobId,
            Direction = request.Direction,
            CreatedBy = user.GetSubject(),
            InputObjectKey = $"{S3ObjectKeyPrefix}{jobId}/input.csv",
            OriginalFileName = request.OriginalFileName,
            Encoding = request.Encoding,
            Delimiter = request.Delimiter,
            HasHeaderRow = request.HasHeaderRow,
            ColumnMappings = [.. request.ColumnMappings],
            CreatedAt = now,
            LastUpdatedAt = now,
        };

        await jobRepository.CreateAsync(job, cancellationToken);

        var uploadUrl = await s3.GetPreSignedURLAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = Config.Bucket,
                Key = job.InputObjectKey,
                Verb = HttpVerb.PUT,
                Protocol = PresignedUrlProtocol,
                Expires = DateTime.UtcNow.Add(Config.PresignedUrlExpiry),
                ContentType = "text/csv",
            }
        );

        return (job, uploadUrl);
    }

    /// <inheritdoc/>
    public async Task<PseudonymizationJob> CreateExportJobAsync(
        CreateCsvExportJobRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        await EnsureNamespaceAccessAsync(
            [request.NamespaceName],
            PseudonymizationJobDirection.Export,
            user,
            cancellationToken
        );

        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var job = new PseudonymizationJob
        {
            Id = jobId,
            Direction = PseudonymizationJobDirection.Export,
            CreatedBy = user.GetSubject(),
            // No input object at all - an export's rows come from the database. Every other
            // direction's "wait for the upload, then verify it landed" dance exists only to get
            // one, so an export skips straight past AwaitingUpload to Queued. TotalBytes stays 0
            // for the same reason, which is what makes the UI show rows rather than a percentage.
            InputObjectKey = null,
            Status = PseudonymizationJobStatus.Queued,
            Encoding = request.Encoding,
            Delimiter = request.Delimiter,
            // Always written, so an exported file can be imported straight back without the
            // operator having to restate which column is which.
            HasHeaderRow = true,
            ColumnMappings =
            [
                new ColumnMapping
                {
                    SourceColumn = request.OriginalValueColumn,
                    TargetColumn = request.PseudonymValueColumn,
                    Namespace = request.NamespaceName,
                },
            ],
            CreatedAt = now,
            LastUpdatedAt = now,
        };

        await jobRepository.CreateAsync(job, cancellationToken);
        await EnqueueAsync(job, cancellationToken);

        return job;
    }

    /// <inheritdoc/>
    public async Task MarkUploadCompleteAsync(
        Guid jobId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var job = await GetOwnedJobAsync(jobId, user, cancellationToken);

        // Re-checked, not taken on trust from CreateJobAsync: this is the call that actually
        // sets the job running, and a grant can have been revoked in between - including while
        // the caller's upload was in flight, which for a large file is a long time.
        await EnsureCurrentNamespaceAccessAsync(job, user, cancellationToken);

        long totalBytes;
        try
        {
            var metadata = await s3.GetObjectMetadataAsync(
                Config.Bucket,
                job.InputObjectKey,
                cancellationToken
            );
            totalBytes = metadata.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // The client only *claims* its PUT succeeded - never trust that without checking.
            throw new InvalidOperationException(
                "The uploaded input file could not be found in object storage."
            );
        }

        await jobRepository.MarkQueuedAsync(jobId, totalBytes, cancellationToken);
        await EnqueueAsync(job, cancellationToken);
    }

    private async Task EnqueueAsync(PseudonymizationJob job, CancellationToken cancellationToken)
    {
        var hangfireJobId = backgroundJobClient.Enqueue<ICsvPseudonymizationJobRunner>(runner =>
            runner.RunAsync(job.Id, BuildJobLabel(job), JobCancellationToken.Null)
        );
        await jobRepository.SetHangfireJobIdAsync(job.Id, hangfireJobId, cancellationToken);
    }

    // Matches the wording of CsvJobs.razor's own Direction column, so the same job reads
    // consistently whether you're looking at it in vfps's UI or in the Hangfire dashboard.
    private static string BuildJobLabel(PseudonymizationJob job)
    {
        var direction = job.Direction switch
        {
            PseudonymizationJobDirection.Depseudonymize => "De-pseudonymize",
            PseudonymizationJobDirection.Import => "Import",
            PseudonymizationJobDirection.Export => "Export",
            _ => "Pseudonymize",
        };

        // An export has no file name to show, so it's labelled with the one thing that actually
        // identifies it instead: the namespace being exported.
        var subject =
            job.Direction == PseudonymizationJobDirection.Export
                ? ResolveSingleNamespaceMapping(job).Namespace
                : job.OriginalFileName;

        return subject is null ? direction : $"{direction} - {subject}";
    }

    /// <summary>
    /// Reads back the single <see cref="ColumnMapping"/> an
    /// <see cref="PseudonymizationJobDirection.Import"/>/<see cref="PseudonymizationJobDirection.Export"/>
    /// job carries - see that type's own doc comment for why those two directions reuse it rather
    /// than adding columns to <see cref="PseudonymizationJob"/>. Throws rather than returning null
    /// for a job that somehow has none (or several): both are impossible through this service's
    /// own creation paths, so either means a hand-edited row, and processing it as though it named
    /// some other namespace would be far worse than failing the job.
    /// </summary>
    public static ColumnMapping ResolveSingleNamespaceMapping(PseudonymizationJob job) =>
        job.ColumnMappings.Count == 1
            ? job.ColumnMappings[0]
            : throw new InvalidOperationException(
                $"A {job.Direction} job must have exactly one column mapping, but job "
                    + $"'{job.Id}' has {job.ColumnMappings.Count}."
            );

    /// <inheritdoc/>
    public async Task<PseudonymizationJob> GetAsync(
        Guid jobId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var job = await GetOwnedJobAsync(jobId, user, cancellationToken);
        await EnsureCurrentNamespaceAccessAsync(job, user, cancellationToken);

        return job;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PseudonymizationJob>> ListAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var createdBy = permissionChecker.IsAdmin(user) ? null : user.GetSubject();
        return await jobRepository.ListAsync(createdBy, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task CancelAsync(
        Guid jobId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var job = await GetOwnedJobAsync(jobId, user, cancellationToken);

        if (
            job.Status
            is PseudonymizationJobStatus.Completed
                or PseudonymizationJobStatus.Failed
                or PseudonymizationJobStatus.Cancelled
                or PseudonymizationJobStatus.Stalled
        )
        {
            return;
        }

        // Removes it from Hangfire's queue if it hasn't started running yet. If it's already
        // running, this is a no-op - the runner cooperatively checks Status between rows instead.
        if (job.HangfireJobId is not null)
        {
            backgroundJobClient.Delete(job.HangfireJobId);
        }

        await jobRepository.UpdateStatusAsync(
            jobId,
            PseudonymizationJobStatus.Cancelled,
            null,
            cancellationToken
        );
    }

    /// <inheritdoc/>
    public async Task<string> GetDownloadUrlAsync(
        Guid jobId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var job = await GetOwnedJobAsync(jobId, user, cancellationToken);

        // The output is the sensitive artefact - for a de-pseudonymization job it is the original
        // values themselves - and it stays in the bucket until the retention rule expires it
        // (S3Config.ObjectRetentionDays, 30 days by default). Ownership alone must not keep it
        // downloadable: without this, revoking someone's reverse-lookup access would leave every
        // job they ran while they held it still fetchable, one presigned URL at a time.
        await EnsureCurrentNamespaceAccessAsync(job, user, cancellationToken);

        if (job.Status != PseudonymizationJobStatus.Completed || job.OutputObjectKey is null)
        {
            throw new InvalidOperationException(
                "The job has not completed yet - no output file is available."
            );
        }

        return await s3.GetPreSignedURLAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = Config.Bucket,
                Key = job.OutputObjectKey,
                Verb = HttpVerb.GET,
                Protocol = PresignedUrlProtocol,
                Expires = DateTime.UtcNow.Add(Config.PresignedUrlExpiry),
                // The object itself always lives at the fixed, deterministic
                // "csv-jobs/{jobId}/output.csv" key (see CsvPseudonymizationJobRunner) regardless
                // of the input's name - this only controls what the browser offers to save the
                // download as, via a signed "response-content-disposition" query parameter S3
                // honors for presigned GET URLs.
                ResponseHeaderOverrides = new ResponseHeaderOverrides
                {
                    ContentDisposition = new ContentDisposition
                    {
                        FileName = BuildOutputFileName(job),
                    }.ToString(),
                },
            }
        );
    }

    // The original upload's own extension is discarded, not preserved - the output is always a
    // CSV regardless of what the input happened to be named/extended as.
    private static string BuildOutputFileName(PseudonymizationJob job)
    {
        // An export has no uploaded file to name itself after; the namespace is what identifies
        // it, and it's also what makes a folder full of exports tellable apart.
        if (job.Direction == PseudonymizationJobDirection.Export)
        {
            return $"{ResolveSingleNamespaceMapping(job).Namespace}-export.csv";
        }

        var baseName = job.OriginalFileName is null
            ? job.Id.ToString()
            : Path.GetFileNameWithoutExtension(job.OriginalFileName);
        var suffix = job.Direction switch
        {
            PseudonymizationJobDirection.Depseudonymize => "de-pseudonymized",
            // Not "imported": the download is the per-row outcome report, not the data that was
            // imported (which is the file the operator uploaded and still has).
            PseudonymizationJobDirection.Import => "import-report",
            _ => "pseudonymized",
        };
        return $"{baseName}-{suffix}.csv";
    }

    /// <inheritdoc/>
    public async Task<int> ClearFinishedAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var createdBy = permissionChecker.IsAdmin(user) ? null : user.GetSubject();
        return await jobRepository.DeleteFinishedAsync(createdBy, cancellationToken);
    }

    /// <summary>
    /// Checks that the caller holds, right now, the access this job's own column mappings demand.
    ///
    /// Permissions are checked at creation, but a grant can be revoked at any point in a job's
    /// life and ownership alone must not keep the door open afterwards - so every operation that
    /// acts on a stored job re-checks rather than trusting the check made when it was submitted.
    /// ListAsync and CancelAsync deliberately don't: seeing a row for your own job (its file
    /// name, status and counts - never a pseudonym or an original value) is not access to the
    /// data, and being able to stop a job you started is a de-escalation, not a privilege.
    /// </summary>
    private Task EnsureCurrentNamespaceAccessAsync(
        PseudonymizationJob job,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    ) =>
        EnsureNamespaceAccessAsync(
            job.ColumnMappings.Select(m => m.Namespace),
            job.Direction,
            user,
            cancellationToken
        );

    private async Task EnsureNamespaceAccessAsync(
        IEnumerable<string> namespaceNames,
        PseudonymizationJobDirection direction,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        // Resolved once for the whole job: a job's column mappings routinely span several
        // namespaces, and every one of them is checked against the same caller.
        var permissions = await permissionChecker.ResolveAsync(user, cancellationToken);

        // Depseudonymize and Export both hand back original values, so they're gated the same as
        // the manual reverse-lookup textbox (reverse-lookup access), not merely write access -
        // an export is a bulk reverse lookup of the whole namespace in one file. Import only ever
        // stores pairs the caller already holds, so write access is the right bar for it, exactly
        // as for Pseudonymize.
        Func<string, bool> hasAccess = direction switch
        {
            PseudonymizationJobDirection.Depseudonymize or PseudonymizationJobDirection.Export =>
                permissions.HasReverseLookupAccess,
            _ => permissions.HasWriteAccess,
        };
        var requiredAccessDescription = direction switch
        {
            PseudonymizationJobDirection.Depseudonymize or PseudonymizationJobDirection.Export =>
                "Reverse-lookup",
            _ => "Write",
        };

        foreach (
            var namespaceName in namespaceNames
                .Distinct(StringComparer.Ordinal)
                .Where(namespaceName => !hasAccess(namespaceName))
        )
        {
            throw new ForbiddenException(
                $"{requiredAccessDescription} access to namespace '{namespaceName}' is required."
            );
        }
    }

    private async Task<PseudonymizationJob> GetOwnedJobAsync(
        Guid jobId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var job =
            await jobRepository.FindAsync(jobId, cancellationToken)
            ?? throw new PseudonymizationJobNotFoundException(jobId);
        if (job.CreatedBy != user.GetSubject() && !permissionChecker.IsAdmin(user))
        {
            throw new ForbiddenException(
                "Only the job's creator, or an admin, can access this job."
            );
        }

        return job;
    }
}
