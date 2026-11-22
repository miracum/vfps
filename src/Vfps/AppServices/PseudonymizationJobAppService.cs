using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Security.Claims;
using Amazon.S3;
using Amazon.S3.Model;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Hangfire;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Vfps.Authorization;
using Vfps.Config;
using Vfps.CsvProcessing;
using Vfps.Data;
using Vfps.Data.Models;
using Vfps.Protos;

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
    /// The Content-Type every input upload is signed with - and so the one a client's PUT has to
    /// send, see <see cref="PresignedUrl.Headers"/>.
    /// </summary>
    public const string InputContentType = "text/csv";

    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 1000;

    private S3Config Config => s3Config.Value;

    // GetPreSignedUrlRequest.Protocol defaults to https regardless of the S3 client's own
    // ServiceURL/UseHttp configuration - without this, a plain-HTTP endpoint (e.g. local MinIO)
    // would still get its presigned URLs signed as "https://", which browsers then fail to load.
    private Protocol PresignedUrlProtocol =>
        Config.ServiceUrl.StartsWith("http://", StringComparison.Ordinal)
            ? Protocol.HTTP
            : Protocol.HTTPS;

    /// <inheritdoc/>
    public async Task<(PseudonymizationJob Job, PresignedUrl Upload)> CreateJobAsync(
        CreateCsvJobRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        CsvJobFormat.ParseDelimiter(request.Delimiter);

        if (
            request.PseudonymizeMode != PseudonymizeMode.CreateIfMissing
            && request.Direction != PseudonymizationJobDirection.Pseudonymize
        )
        {
            throw new ArgumentException(
                $"A {request.Direction} batch has no pseudonymize mode - "
                    + $"{nameof(request.PseudonymizeMode)} is only valid on a "
                    + $"{PseudonymizationJobDirection.Pseudonymize} batch.",
                nameof(request)
            );
        }

        await EnsureNamespaceAccessAsync(
            request.ColumnMappings,
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
            PseudonymizeMode = request.PseudonymizeMode,
            CreatedBy = user.GetSubject(),
            InputObjectKey = $"{Config.NormalizedObjectKeyPrefix}{jobId}/input.csv",
            OriginalFileName = request.OriginalFileName,
            Encoding = CsvFileEncodings.ToName(request.Encoding),
            Delimiter = request.Delimiter,
            HasHeaderRow = request.HasHeaderRow,
            ColumnMappings = [.. request.ColumnMappings],
            CreatedAt = now,
            LastUpdatedAt = now,
        };

        await jobRepository.CreateAsync(job, cancellationToken);

        var expiresAt = DateTimeOffset.UtcNow.Add(Config.PresignedUrlExpiry);
        var uploadUrl = await s3.GetPreSignedURLAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = Config.Bucket,
                Key = job.InputObjectKey,
                Verb = HttpVerb.PUT,
                Protocol = PresignedUrlProtocol,
                Expires = expiresAt.UtcDateTime,
                ContentType = InputContentType,
            }
        );

        return (
            job,
            new PresignedUrl(
                "PUT",
                uploadUrl,
                new Dictionary<string, string> { ["Content-Type"] = InputContentType },
                expiresAt
            )
        );
    }

    /// <inheritdoc/>
    public async Task<PseudonymizationJob> CreateExportJobAsync(
        CreateCsvExportJobRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        CsvJobFormat.ParseDelimiter(request.Delimiter);

        await EnsureNamespaceAccessAsync(
            [
                new ColumnMapping
                {
                    SourceColumn = request.OriginalValueColumn,
                    TargetColumn = request.PseudonymValueColumn,
                    Namespace = request.NamespaceName,
                },
            ],
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
            Encoding = CsvFileEncodings.ToName(request.Encoding),
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

        // Idempotent no-op past this point, not just on trust: this is now reachable directly
        // over HTTP from the browser (see the /batches/{id}/upload-complete endpoint) rather
        // than only from one awaited call in Batches.razor, so a retried request (a flaky network
        // between browser and server, a duplicate fetch) is a real possibility, not a hypothetical
        // one. EnqueueAsync below has no such guard of its own - it would otherwise enqueue a
        // second Hangfire job that reprocesses the same input from scratch.
        if (job.Status != PseudonymizationJobStatus.AwaitingUpload)
        {
            return;
        }

        if (!await TryQueueUploadedJobAsync(job, cancellationToken))
        {
            // The client only *claims* its PUT succeeded - never trust that without checking.
            throw new InvalidOperationException(
                "The uploaded input file could not be found in object storage."
            );
        }
    }

    /// <inheritdoc/>
    public async Task<PseudonymizationJob> PollAsync(
        Guid jobId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var job = await GetAsync(jobId, user, cancellationToken);
        if (job.Status != PseudonymizationJobStatus.AwaitingUpload)
        {
            return job;
        }

        // Re-read whether or not this call was the one to queue it: a concurrent poll (or the
        // UI's upload-complete endpoint) may have won the transition, and either way the caller
        // should see the job as it is now rather than the AwaitingUpload it was a moment ago.
        return await TryQueueUploadedJobAsync(job, cancellationToken)
            ? await ReloadAsync(jobId, cancellationToken)
            : job;
    }

    /// <summary>
    /// Queues an <see cref="PseudonymizationJobStatus.AwaitingUpload"/> job if its input object
    /// exists, returning false - and leaving the job alone - if it doesn't. Returns true as well
    /// when the object exists but another request won the transition: either way the job is no
    /// longer waiting for its upload.
    /// </summary>
    private async Task<bool> TryQueueUploadedJobAsync(
        PseudonymizationJob job,
        CancellationToken cancellationToken
    )
    {
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
            return false;
        }

        // The callers' status checks only spare a repeat request the S3 round trip. Two requests
        // that arrive together - possibly on different replicas - can both pass them, so this is
        // what actually decides which one gets to enqueue: only the request whose transition out
        // of AwaitingUpload matched does.
        if (await jobRepository.MarkQueuedAsync(job.Id, totalBytes, cancellationToken))
        {
            await EnqueueAsync(job, cancellationToken);
        }

        return true;
    }

    private async Task EnqueueAsync(PseudonymizationJob job, CancellationToken cancellationToken)
    {
        var hangfireJobId = backgroundJobClient.Enqueue<ICsvPseudonymizationJobRunner>(runner =>
            runner.RunAsync(job.Id, BuildJobLabel(job), JobCancellationToken.Null)
        );
        await jobRepository.SetHangfireJobIdAsync(job.Id, hangfireJobId, cancellationToken);
    }

    // Matches the wording of Batches.razor's own Direction column, so the same job reads
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
    public async Task<PseudonymizationJobPage> ListPageAsync(
        ClaimsPrincipal user,
        int pageSize,
        string? pageToken,
        CancellationToken cancellationToken
    )
    {
        var take = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
        var after = DecodeCursor(pageToken);
        var createdBy = permissionChecker.IsAdmin(user) ? null : user.GetSubject();

        var jobs = await jobRepository.ListPageAsync(createdBy, after, take, cancellationToken);

        // A full page may be followed by an empty one - the price of not counting, which costs a
        // client at most one extra request.
        var nextPageToken =
            jobs.Count == take
                ? EncodeCursor(new PseudonymizationJobPageCursor(jobs[^1].CreatedAt, jobs[^1].Id))
                : null;

        return new PseudonymizationJobPage(jobs, nextPageToken);
    }

    private static PseudonymizationJobPageCursor? DecodeCursor(string? pageToken)
    {
        if (string.IsNullOrEmpty(pageToken))
        {
            return null;
        }

        try
        {
            var token = BatchListPaginationToken.Parser.ParseFrom(
                WebEncoders.Base64UrlDecode(pageToken)
            );
            return new PseudonymizationJobPageCursor(
                token.CreatedAt.ToDateTimeOffset(),
                Guid.Parse(token.Id)
            );
        }
        catch (Exception ex)
            when (ex is FormatException or InvalidProtocolBufferException or NullReferenceException)
        {
            throw new ArgumentException("The page token is not valid.", nameof(pageToken), ex);
        }
    }

    private static string EncodeCursor(PseudonymizationJobPageCursor cursor)
    {
        var token = new BatchListPaginationToken
        {
            CreatedAt = Timestamp.FromDateTimeOffset(cursor.CreatedAt),
            Id = cursor.Id.ToString(),
        };

        return WebEncoders.Base64UrlEncode(token.ToByteArray());
    }

    /// <inheritdoc/>
    public async Task<PseudonymizationJob> CancelAsync(
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
            return job;
        }

        // Conditional, so a job that finished between the check above and this write keeps its
        // real outcome rather than being relabelled Cancelled - and so its Hangfire job is left
        // alone below.
        if (!await jobRepository.MarkCancelledAsync(jobId, cancellationToken))
        {
            return await ReloadAsync(jobId, cancellationToken);
        }

        // Removes it from Hangfire's queue if it hasn't started running yet. If it's already
        // running, this is a no-op - the runner cooperatively checks Status between rows instead.
        // After the status write rather than before it: a worker that picks the job up in between
        // already sees Cancelled and stops.
        if (job.HangfireJobId is not null)
        {
            backgroundJobClient.Delete(job.HangfireJobId);
        }

        return await ReloadAsync(jobId, cancellationToken);
    }

    private async Task<PseudonymizationJob> ReloadAsync(
        Guid jobId,
        CancellationToken cancellationToken
    ) =>
        await jobRepository.FindAsync(jobId, cancellationToken)
        ?? throw new PseudonymizationJobNotFoundException(jobId);

    /// <inheritdoc/>
    public async Task<PresignedUrl> GetDownloadUrlAsync(
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
                "The batch has not completed yet - no output file is available."
            );
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(Config.PresignedUrlExpiry);
        var downloadUrl = await s3.GetPreSignedURLAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = Config.Bucket,
                Key = job.OutputObjectKey,
                Verb = HttpVerb.GET,
                Protocol = PresignedUrlProtocol,
                Expires = expiresAt.UtcDateTime,
                // The object itself always lives at the fixed, deterministic
                // "{S3:ObjectKeyPrefix}{jobId}/output.csv" key (see CsvPseudonymizationJobRunner) regardless
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

        return new PresignedUrl("GET", downloadUrl, new Dictionary<string, string>(), expiresAt);
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

    /// <inheritdoc/>
    public async Task DeleteAsync(
        Guid jobId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        // Ownership only, no namespace re-check - the same as CancelAsync and ClearFinishedAsync:
        // removing your own job's row reveals and grants nothing.
        await GetOwnedJobAsync(jobId, user, cancellationToken);

        // Decided by the DELETE's own predicate, not by the status just read: the job may have
        // been queued or finished in between.
        if (!await jobRepository.DeleteIfFinishedAsync(jobId, cancellationToken))
        {
            throw new InvalidOperationException(
                "Only a finished batch can be deleted - cancel it first."
            );
        }
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
    ) => EnsureNamespaceAccessAsync(job.ColumnMappings, job.Direction, user, cancellationToken);

    private async Task EnsureNamespaceAccessAsync(
        IReadOnlyList<ColumnMapping> columnMappings,
        PseudonymizationJobDirection direction,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        // Resolved once for the whole job: a job's column mappings routinely span several
        // namespaces, and every one of them is checked against the same caller.
        var permissions = await permissionChecker.ResolveAsync(user, cancellationToken);

        // A namespace-column import names its target namespaces inside the file, which nothing
        // has read yet - there is no set of names to check here, and by the time there is, the
        // caller is long gone and the runner has no principal to re-check against (the same
        // reason IPseudonymAppService's Trusted methods exist at all). The only permission that
        // can be verified up front and still be true for whatever the file turns out to name is
        // write access to every namespace, so that is what this asks for. Anything narrower would
        // amount to checking nothing.
        if (columnMappings.Any(m => !string.IsNullOrEmpty(m.NamespaceColumn)))
        {
            if (!permissions.HasWriteAccessToAllNamespaces)
            {
                throw new ForbiddenException(
                    "Importing into the namespace named by a column requires write access to "
                        + "every namespace, since the namespaces the file names are only known "
                        + "once it is processed. Grant access without a namespace, or import "
                        + "into a single chosen namespace instead."
                );
            }

            return;
        }

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
            var namespaceName in columnMappings
                .Select(m => m.Namespace)
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
                "Only the batch's creator, or an admin, can access this batch."
            );
        }

        return job;
    }
}
