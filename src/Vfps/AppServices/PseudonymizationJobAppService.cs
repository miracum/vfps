using System.Linq;
using System.Net;
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
    /// under this prefix) and <see cref="S3LifecyclePolicyBackgroundService"/> (which scopes the
    /// bucket's expiration rule to it), so all three stay in sync on a single literal.
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
        foreach (
            var namespaceName in request
                .ColumnMappings.Select(m => m.Namespace)
                .Distinct()
                .Where(namespaceName => !permissionChecker.HasWriteAccess(user, namespaceName))
        )
        {
            throw new ForbiddenException(
                $"Write access to namespace '{namespaceName}' is required."
            );
        }

        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var job = new PseudonymizationJob
        {
            Id = jobId,
            CreatedBy = GetSubject(user),
            InputObjectKey = $"{S3ObjectKeyPrefix}{jobId}/input.csv",
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
    public async Task MarkUploadCompleteAsync(
        Guid jobId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var job = await GetOwnedJobAsync(jobId, user, cancellationToken);

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

        var hangfireJobId = backgroundJobClient.Enqueue<ICsvPseudonymizationJobRunner>(runner =>
            runner.RunAsync(jobId, JobCancellationToken.Null)
        );
        await jobRepository.SetHangfireJobIdAsync(jobId, hangfireJobId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<PseudonymizationJob> GetAsync(
        Guid jobId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    ) => await GetOwnedJobAsync(jobId, user, cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PseudonymizationJob>> ListAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var createdBy = permissionChecker.IsAdmin(user) ? null : GetSubject(user);
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
            }
        );
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
        if (job.CreatedBy != GetSubject(user) && !permissionChecker.IsAdmin(user))
        {
            throw new ForbiddenException(
                "Only the job's creator, or an admin, can access this job."
            );
        }

        return job;
    }

    private static string GetSubject(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
}
