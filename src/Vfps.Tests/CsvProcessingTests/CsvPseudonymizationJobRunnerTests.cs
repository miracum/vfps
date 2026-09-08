using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using FakeItEasy;
using Hangfire;
using Hangfire.Server;
using Hangfire.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vfps.AppServices;
using Vfps.Config;
using Vfps.CsvProcessing;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.Tests.CsvProcessingTests;

// Most of these tests verify the row-transform logic (which namespace/value each field resolves
// to, in what order, and how many rows get processed) via the calls made to pseudonymAppService/
// jobRepository. Output content can now also be asserted on directly - see CaptureOutputParts:
// the runner drives the multipart upload itself rather than handing a pipe to TransferUtility,
// which used to read internal client config off IAmazonS3.Config that FakeItEasy auto-fakes to
// zeroed-out values, producing a bogus empty upload that made such assertions meaningless.
public class CsvPseudonymizationJobRunnerTests
{
    private const string Bucket = "test-bucket";

    private readonly IPseudonymizationJobRepository jobRepository =
        A.Fake<IPseudonymizationJobRepository>();
    private readonly IPseudonymAppService pseudonymAppService = A.Fake<IPseudonymAppService>();
    private readonly INamespaceRepository namespaceRepository = A.Fake<INamespaceRepository>();
    private readonly IAmazonS3 s3 = A.Fake<IAmazonS3>();

    // Hangfire.JobCancellationToken.Null's own ShutdownToken getter throws
    // NullReferenceException (a real quirk of that library, not something under test) - a fake
    // with ShutdownToken wired to a real, non-cancelled CancellationToken is what every other
    // caller of this runner effectively gets in production instead.
    private static IJobCancellationToken CreateCancellationToken()
    {
        var token = A.Fake<IJobCancellationToken>();
        A.CallTo(() => token.ShutdownToken).Returns(CancellationToken.None);
        return token;
    }

    // A real PerformContext, not a FakeItEasy fake - PerformContext is a concrete class whose
    // SetJobParameter isn't virtual, but it only ever delegates to Connection.SetJobParameter
    // (IStorageConnection, an interface), so faking that one dependency and constructing the
    // context for real lets tests verify what CsvPseudonymizationJobRunner actually sets.
    private static PerformContext CreatePerformContext(out IStorageConnection connection)
    {
        connection = A.Fake<IStorageConnection>();
        var backgroundJob = new BackgroundJob("test-hangfire-job-id", null, DateTime.UtcNow);
        return new PerformContext(null, connection, backgroundJob, CreateCancellationToken());
    }

    // Defaults to 20 (not CsvProcessingConfig's own production default of 1000) so existing tests
    // exercising "a full chunk plus a trailing partial one" stay meaningful without needing
    // hundreds of rows - pass an explicit value to test PseudonymizeBatchSize-specific behavior.
    // missingValuePlaceholders defaults to CsvProcessingConfig's own production default ("NA"/
    // "NULL") rather than null/empty, so existing tests that don't care about this setting still
    // exercise the same behavior a real deployment would see out of the box.
    private CsvPseudonymizationJobRunner CreateSut(
        int pseudonymizeBatchSize = 20,
        List<string>? missingValuePlaceholders = null,
        int? outputPartSizeBytes = null
    ) =>
        new(
            jobRepository,
            pseudonymAppService,
            namespaceRepository,
            s3,
            Options.Create(new S3Config { Bucket = Bucket }),
            Options.Create(
                new CsvProcessingConfig
                {
                    PseudonymizeBatchSize = pseudonymizeBatchSize,
                    MissingValuePlaceholders = missingValuePlaceholders ?? ["NA", "NULL"],
                    OutputPartSizeBytes =
                        outputPartSizeBytes ?? new CsvProcessingConfig().OutputPartSizeBytes,
                }
            ),
            NullLogger<CsvPseudonymizationJobRunner>.Instance
        );

    private static Data.Models.Namespace CreateNamespace(string name) =>
        new()
        {
            Name = name,
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
        };

    private static PseudonymizationJob CreateJob(
        PseudonymizationJobDirection direction,
        params ColumnMapping[] columnMappings
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            Status = PseudonymizationJobStatus.Queued,
            Direction = direction,
            CreatedBy = "test-user",
            InputObjectKey = "csv-jobs/input.csv",
            ColumnMappings = [.. columnMappings],
        };

    private void FakeInputObject(PseudonymizationJob job, string csvContent) =>
        A.CallTo(() => s3.GetObjectAsync(Bucket, job.InputObjectKey, A<CancellationToken>._))
            .Returns(
                new GetObjectResponse
                {
                    ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes(csvContent)),
                }
            );

    private const string UploadId = "test-upload-id";

    /// <summary>
    /// Stubs the multipart upload calls and collects the bytes of every uploaded part, so a test
    /// can assert on the output the job actually produced. Parts are copied out inside the
    /// callback because the runner reuses (and clears) one buffer for all of them.
    /// </summary>
    private List<byte[]> CaptureOutputParts()
    {
        var parts = new List<byte[]>();

        A.CallTo(() =>
                s3.InitiateMultipartUploadAsync(
                    A<InitiateMultipartUploadRequest>._,
                    A<CancellationToken>._
                )
            )
            .Returns(new InitiateMultipartUploadResponse { UploadId = UploadId });

        A.CallTo(() => s3.UploadPartAsync(A<UploadPartRequest>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                var request = call.GetArgument<UploadPartRequest>(0)!;
                using var copy = new MemoryStream();
                request.InputStream.CopyTo(copy);
                parts.Add(copy.ToArray());

                return Task.FromResult(
                    new UploadPartResponse
                    {
                        PartNumber = request.PartNumber,
                        ETag = $"etag-{request.PartNumber}",
                    }
                );
            });

        return parts;
    }

    /// <summary>The full output as text, with the encoding's byte-order mark stripped.</summary>
    private static string ReadOutput(List<byte[]> parts) =>
        Encoding.UTF8.GetString([.. parts.SelectMany(part => part)]).TrimStart('\uFEFF');

    private void FakeResumableUpload() =>
        A.CallTo(() => s3.ListPartsAsync(A<ListPartsRequest>._, A<CancellationToken>._))
            .Returns(new ListPartsResponse());

    private void FakeStaleUpload() =>
        A.CallTo(() => s3.ListPartsAsync(A<ListPartsRequest>._, A<CancellationToken>._))
            .Throws(new AmazonS3Exception("no such upload") { ErrorCode = "NoSuchUpload" });

    private void FakeFindJob(PseudonymizationJob job) =>
        A.CallTo(() => jobRepository.FindAsync(job.Id, A<CancellationToken>._)).Returns(job);

    // Backs the CreateTrustedBatchAsync fake below - CsvPseudonymizationJobRunner's Pseudonymize
    // path resolves a whole chunk via one batched call rather than one CreateTrustedAsync call
    // per value (see FlushChunkPseudonymizeAsync), so FakePseudonymize registers known
    // (namespace, originalValue) -> pseudonymValue mappings here instead of stubbing individual
    // calls directly.
    private readonly Dictionary<
        (string Namespace, string OriginalValue),
        string
    > knownPseudonymValues = [];
    private bool batchPseudonymizeFakeConfigured;

    private void FakePseudonymize(string namespaceName, string originalValue, string pseudonymValue)
    {
        knownPseudonymValues[(namespaceName, originalValue)] = pseudonymValue;

        if (batchPseudonymizeFakeConfigured)
        {
            return;
        }

        batchPseudonymizeFakeConfigured = true;
        A.CallTo(() =>
                pseudonymAppService.CreateTrustedBatchAsync(
                    A<IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(call =>
            {
                var requests = call.GetArgument<
                    IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)>
                >(0)!;

                var result = new Dictionary<(string, string), Data.Models.Pseudonym>();
                foreach (var (ns, originalValue) in requests)
                {
                    var key = (ns.Name, originalValue);
                    result[key] = new Data.Models.Pseudonym
                    {
                        NamespaceName = ns.Name,
                        OriginalValue = originalValue,
                        PseudonymValue = knownPseudonymValues[key],
                    };
                }

                return Task.FromResult(
                    (IReadOnlyDictionary<(string, string), Data.Models.Pseudonym>)result
                );
            });
    }

    private void FakeDepseudonymize(
        string namespaceName,
        string pseudonymValue,
        string? originalValue
    ) =>
        A.CallTo(() =>
                pseudonymAppService.ReverseLookupTrustedAsync(
                    namespaceName,
                    pseudonymValue,
                    A<CancellationToken>._
                )
            )
            .Returns(
                originalValue is null
                    ? null
                    : new Data.Models.Pseudonym
                    {
                        NamespaceName = namespaceName,
                        OriginalValue = originalValue,
                        PseudonymValue = pseudonymValue,
                    }
            );

    [Fact]
    public async Task RunAsync_ShouldWriteTheTransformedRowsToTheOutputUpload()
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,secret\n2,other\n");
        FakePseudonymize("ns", "secret", "pseudonym-of-secret");
        FakePseudonymize("ns", "other", "pseudonym-of-other");
        var parts = CaptureOutputParts();

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        ReadOutput(parts)
            .Should()
            .Be("id,value\r\n1,pseudonym-of-secret\r\n2,pseudonym-of-other\r\n");
        A.CallTo(() =>
                s3.CompleteMultipartUploadAsync(
                    A<CompleteMultipartUploadRequest>.That.Matches(r => r.UploadId == UploadId),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_OnceEnoughOutputAccumulates_ShouldUploadAPartAndCheckpointIt()
    {
        // The only test that produces enough output for a part to actually be cut (S3's 5 MiB
        // minimum is a hard floor, so this can't be made cheap by shrinking the part size). Rows
        // are wide rather than numerous to keep it fast, and every row carries the same value so a
        // single batched resolve covers all of them.
        var wideValue = new string('x', 1000);
        var widePseudonym = new string('p', 1000);
        var rowCount = 6000;
        var input = new StringBuilder("id,value\n");
        for (var i = 0; i < rowCount; i++)
        {
            input.Append(i).Append(',').Append(wideValue).Append('\n');
        }

        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, input.ToString());
        FakePseudonymize("ns", wideValue, widePseudonym);
        var parts = CaptureOutputParts();

        var sut = CreateSut(
            pseudonymizeBatchSize: rowCount,
            outputPartSizeBytes: MultipartOutputStream.MinimumPartSizeBytes
        );
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        parts.Should().ContainSingle();
        parts[0].Length.Should().BeGreaterThanOrEqualTo(MultipartOutputStream.MinimumPartSizeBytes);

        // The checkpoint has to name the rows whose output is inside the part that was just
        // stored - that pairing is what makes resuming correct rather than approximate.
        A.CallTo(() =>
                jobRepository.SaveOutputCheckpointAsync(
                    job.Id,
                    A<JobOutputCheckpoint>.That.Matches(checkpoint =>
                        checkpoint.UploadId == UploadId
                        && checkpoint.RowsWritten == rowCount
                        && checkpoint.Parts.Count == 1
                        && checkpoint.Parts[0].ETag == "etag-1"
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();

        // ...and it must be gone once the job can no longer be resumed from it.
        A.CallTo(() => jobRepository.ClearOutputCheckpointAsync(job.Id, A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task RunAsync_WithACheckpoint_ShouldNotReprocessTheRowsItCovers()
    {
        // The point of checkpointing: an interrupted job picks up where it stopped instead of
        // redoing the (expensive) pseudonymization for rows whose output is already stored.
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        job.OutputCheckpoint = new JobOutputCheckpoint
        {
            UploadId = UploadId,
            ObjectKey = $"csv-jobs/{job.Id}/output.csv",
            Parts = [new CompletedOutputPart { PartNumber = 1, ETag = "etag-1" }],
            RowsWritten = 1,
        };
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,already-done\n2,still-to-do\n");
        FakePseudonymize("ns", "still-to-do", "pseudonym-of-still-to-do");
        FakeResumableUpload();
        var parts = CaptureOutputParts();

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.CreateTrustedBatchAsync(
                    A<
                        IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)>
                    >.That.Matches(reqs => reqs.Any(r => r.OriginalValue == "already-done")),
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();

        // Neither the byte-order mark nor the header is re-emitted - both are already inside the
        // part the checkpoint refers to, so repeating them would corrupt the middle of the file.
        var written = Encoding.UTF8.GetString([.. parts.SelectMany(part => part)]);
        written.Should().Be("2,pseudonym-of-still-to-do\r\n");

        A.CallTo(() =>
                s3.InitiateMultipartUploadAsync(
                    A<InitiateMultipartUploadRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task RunAsync_WithACheckpointWhoseUploadIsGone_ShouldStartOver()
    {
        // The bucket lifecycle rule aborts incomplete uploads eventually, so a checkpoint can
        // outlive the parts it points at. That has to degrade to a full reprocess rather than
        // failing the job at CompleteMultipartUpload after all the work has been redone.
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        job.OutputCheckpoint = new JobOutputCheckpoint
        {
            UploadId = "long-gone",
            ObjectKey = $"csv-jobs/{job.Id}/output.csv",
            Parts = [new CompletedOutputPart { PartNumber = 1, ETag = "etag-1" }],
            RowsWritten = 1,
        };
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,first\n2,second\n");
        FakePseudonymize("ns", "first", "pseudonym-of-first");
        FakePseudonymize("ns", "second", "pseudonym-of-second");
        FakeStaleUpload();
        var parts = CaptureOutputParts();

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() => jobRepository.ClearOutputCheckpointAsync(job.Id, A<CancellationToken>._))
            .MustHaveHappened();
        ReadOutput(parts)
            .Should()
            .Be("id,value\r\n1,pseudonym-of-first\r\n2,pseudonym-of-second\r\n");
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 2, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WhenProcessingFails_ShouldAbortTheOutputUpload()
    {
        // A failed attempt is terminal (automatic retries are disabled), so leaving the upload
        // open would strand parts that nothing will ever complete or resume.
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "missing-ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("missing-ns", A<CancellationToken>._))
            .Returns<Data.Models.Namespace?>(null);
        FakeInputObject(job, "id,value\n1,secret\n");
        CaptureOutputParts();

        var sut = CreateSut();
        var act = async () => await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        await act.Should().ThrowAsync<InvalidOperationException>();
        A.CallTo(() =>
                s3.AbortMultipartUploadAsync(
                    A<AbortMultipartUploadRequest>.That.Matches(r => r.UploadId == UploadId),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithPseudonymizeDirection_ShouldPseudonymizeEachRowValueAndComplete()
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,secret\n");
        FakePseudonymize("ns", "secret", "pseudonym-of-secret");

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.CreateTrustedBatchAsync(
                    A<
                        IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)>
                    >.That.Matches(reqs =>
                        reqs.Any(r => r.Namespace.Name == "ns" && r.OriginalValue == "secret")
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 1, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                jobRepository.UpdateStatusAsync(
                    job.Id,
                    PseudonymizationJobStatus.Running,
                    null,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithPerformContext_ShouldSetInputAndOutputObjectKeyAsJobParameters()
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,secret\n");
        FakePseudonymize("ns", "secret", "pseudonym-of-secret");
        var context = CreatePerformContext(out var storageConnection);

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken(), context);

        A.CallTo(() =>
                storageConnection.SetJobParameter(
                    context.BackgroundJob.Id,
                    "InputObjectKey",
                    A<string>.That.Contains(job.InputObjectKey)
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                storageConnection.SetJobParameter(
                    context.BackgroundJob.Id,
                    "OutputObjectKey",
                    A<string>.That.Contains(job.Id.ToString())
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData(PseudonymizationJobStatus.Cancelled)]
    [InlineData(PseudonymizationJobStatus.Failed)]
    [InlineData(PseudonymizationJobStatus.Stalled)]
    public async Task RunAsync_WithStatusChangedByAnotherActorRightAfterProcessing_ShouldNotOverwriteItWithCompleted(
        PseudonymizationJobStatus externallySetStatus
    )
    {
        // Something else - a user's Cancel click, or StalledPseudonymizationJobWatchdogService
        // flagging a false-positive stall - can change the job's status between ProcessAsync
        // finishing and RunAsync's own final status check. Whichever terminal status got there
        // first must win; a late, otherwise-successful finish must not silently overwrite it.
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        var externallyChangedJob = new PseudonymizationJob
        {
            Id = job.Id,
            Status = externallySetStatus,
            Direction = job.Direction,
            CreatedBy = job.CreatedBy,
            InputObjectKey = job.InputObjectKey,
            ColumnMappings = job.ColumnMappings,
        };
        // Only the initial status-guard FindAsync call (the very first one in RunAsync) should
        // see the original, non-terminal job - every call after that, including the final "did
        // this change while we were processing" check, must see the externally-changed one.
        var findCallCount = 0;
        A.CallTo(() => jobRepository.FindAsync(job.Id, A<CancellationToken>._))
            .ReturnsLazily(() => findCallCount++ == 0 ? job : externallyChangedJob);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,secret\n");
        FakePseudonymize("ns", "secret", "pseudonym-of-secret");

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                jobRepository.CompleteAsync(
                    A<Guid>._,
                    A<string>._,
                    A<long>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task RunAsync_WithMalformedCsvField_ShouldReportBadDataRowCountAndStillComplete()
    {
        // A stray, unescaped quote mid-field (e.g. an inch mark in free text) is malformed per
        // strict CSV, but CsvPseudonymizationJobRunner's BadDataFound handler recovers from it -
        // see CsvPseudonymizationJobRunner.ProcessAsync. The row must still be processed (with the
        // literal field content, quote included) and counted, not silently dropped or failed.
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,se\"cret\n");
        FakePseudonymize("ns", "se\"cret", "pseudonym-of-secret");

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.CreateTrustedBatchAsync(
                    A<
                        IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)>
                    >.That.Matches(reqs =>
                        reqs.Any(r => r.Namespace.Name == "ns" && r.OriginalValue == "se\"cret")
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                jobRepository.UpdateProgressAsync(
                    job.Id,
                    A<long>._,
                    1,
                    1,
                    0,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 1, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithBlankOrMissingPlaceholderValues_ShouldSkipPseudonymizingThemAndCompleteJob()
    {
        // A blank cell (or a common "NA"/"NULL" missing-data placeholder, matched case-
        // insensitively) previously crashed the whole job - CreateTrustedBatchAsync rejects a
        // blank original value outright. These must be excluded from the batch and passed
        // through to the output unchanged instead, not fail the whole job over what a real-world
        // CSV export routinely contains.
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,secret\n2,\n3,NA\n4,null\n");
        FakePseudonymize("ns", "secret", "pseudonym-of-secret");

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        // Only the one genuine value should ever reach the batch upsert.
        A.CallTo(() =>
                pseudonymAppService.CreateTrustedBatchAsync(
                    A<
                        IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)>
                    >.That.Matches(reqs => reqs.Count == 1 && reqs[0].OriginalValue == "secret"),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                jobRepository.UpdateProgressAsync(
                    job.Id,
                    A<long>._,
                    4,
                    0,
                    3,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 4, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithCustomMissingValuePlaceholders_ShouldOnlyTreatConfiguredPlaceholdersAsMissing()
    {
        // CsvProcessingConfig.MissingValuePlaceholders is configurable precisely so an operator
        // whose data uses a different (or narrower/wider) set of "no value" conventions than the
        // "NA"/"NULL" default isn't stuck with it - here "NA" is deliberately *not* configured, so
        // it must be treated as a genuine value and reach the batch upsert like any other, while
        // the configured "N/A" is skipped instead.
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,NA\n2,N/A\n");
        FakePseudonymize("ns", "NA", "pseudonym-of-NA");

        var sut = CreateSut(missingValuePlaceholders: ["N/A"]);
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.CreateTrustedBatchAsync(
                    A<
                        IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)>
                    >.That.Matches(reqs => reqs.Count == 1 && reqs[0].OriginalValue == "NA"),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                jobRepository.UpdateProgressAsync(
                    job.Id,
                    A<long>._,
                    2,
                    0,
                    1,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithDepseudonymizeDirection_ShouldReverseLookupEachRowValue()
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Depseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,pseudonym-of-secret\n");
        FakeDepseudonymize("ns", "pseudonym-of-secret", "secret");

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.ReverseLookupTrustedAsync(
                    "ns",
                    "pseudonym-of-secret",
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 1, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithDepseudonymizeDirectionAndNoMatchingPseudonym_ShouldStillCompleteJob()
    {
        // ResolveValueAsync falls back to the raw value when ReverseLookupTrustedAsync returns
        // null (see CsvPseudonymizationJobRunner.ResolveValueAsync) rather than failing the row -
        // this only asserts the job still completes normally in that case, since the runner
        // doesn't expose the per-row output value to verify the fallback value directly.
        var job = CreateJob(
            PseudonymizationJobDirection.Depseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,not-a-known-pseudonym\n");
        FakeDepseudonymize("ns", "not-a-known-pseudonym", null);

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 1, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithDepseudonymizeDirectionAndBlankOrMissingPlaceholderValues_ShouldSkipLookupAndCompleteJob()
    {
        // Blank/"NA"/"NULL" source values already passed through unchanged here (via the same
        // fallback as an unmatched pseudonym - see ResolveValueAsync), so this couldn't crash the
        // way the Pseudonymize path could. It should still skip the DB round trip entirely for
        // these (there's nothing to look up) and count them the same way as the Pseudonymize
        // path, so the UI reports "N missing values" consistently regardless of direction.
        var job = CreateJob(
            PseudonymizationJobDirection.Depseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,pseudonym-of-secret\n2,\n3,NA\n4,null\n");
        FakeDepseudonymize("ns", "pseudonym-of-secret", "secret");

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.ReverseLookupTrustedAsync(
                    "ns",
                    "pseudonym-of-secret",
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                pseudonymAppService.ReverseLookupTrustedAsync(
                    "ns",
                    A<string>.That.Matches(v => v.Length == 0 || v == "NA" || v == "null"),
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                jobRepository.UpdateProgressAsync(
                    job.Id,
                    A<long>._,
                    4,
                    0,
                    3,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 4, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithIndexBasedColumnAndNoHeaderRow_ShouldResolveColumnByIndex()
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "1", Namespace = "ns" }
        );
        job.HasHeaderRow = false;
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "1,secret\n");
        FakePseudonymize("ns", "secret", "pseudonym-of-secret");

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.CreateTrustedBatchAsync(
                    A<
                        IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)>
                    >.That.Matches(reqs => reqs.Any(r => r.OriginalValue == "secret")),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithRowCountNotMultipleOfChunkSize_ShouldStillProcessTrailingPartialChunk()
    {
        // CreateSut's default PseudonymizeBatchSize is 20 - 25 rows exercises one full batch plus
        // a trailing partial one.
        const int rowCount = 25;
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));

        var csv = new StringBuilder("id,value\n");
        for (var i = 0; i < rowCount; i++)
        {
            csv.Append(i).Append(',').Append("value").Append(i).Append('\n');
            FakePseudonymize("ns", $"value{i}", $"pseudonym{i}");
        }
        FakeInputObject(job, csv.ToString());

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        // The last row (index 24) is only reachable if the trailing partial chunk (rows 20-24)
        // actually got flushed - a bug that dropped it would still process the first full chunk.
        A.CallTo(() =>
                pseudonymAppService.CreateTrustedBatchAsync(
                    A<
                        IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)>
                    >.That.Matches(reqs =>
                        reqs.Any(r => r.OriginalValue == $"value{rowCount - 1}")
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                jobRepository.CompleteAsync(job.Id, A<string>._, rowCount, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithColumnNotInHeader_ShouldFailJobWithSanitizedErrorMessage()
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "does_not_exist", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,secret\n");

        var sut = CreateSut();
        var act = () => sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        await act.Should().ThrowAsync<InvalidOperationException>();

        A.CallTo(() =>
                jobRepository.UpdateStatusAsync(
                    job.Id,
                    PseudonymizationJobStatus.Failed,
                    "Processing failed - see server logs for details.",
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithNoHeaderRowAndNonNumericSourceColumn_ShouldFailJobWithSanitizedErrorMessage()
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "not-a-number", Namespace = "ns" }
        );
        job.HasHeaderRow = false;
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "1,secret\n");

        var sut = CreateSut();
        var act = () => sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        await act.Should().ThrowAsync<InvalidOperationException>();

        A.CallTo(() =>
                jobRepository.UpdateStatusAsync(
                    job.Id,
                    PseudonymizationJobStatus.Failed,
                    "Processing failed - see server logs for details.",
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithColumnMappingReferencingUnknownNamespace_ShouldFailJob()
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "does-not-exist" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("does-not-exist", A<CancellationToken>._))
            .Returns((Data.Models.Namespace?)null);
        FakeInputObject(job, "id,value\n1,secret\n");

        var sut = CreateSut();
        var act = () => sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        await act.Should().ThrowAsync<InvalidOperationException>();

        A.CallTo(() =>
                jobRepository.UpdateStatusAsync(
                    job.Id,
                    PseudonymizationJobStatus.Failed,
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WhenServerShutsDownMidProcessing_ShouldNotMarkJobFailed()
    {
        // Regression test for a real incident: a Kubernetes pod restart (e.g. a resource-limit
        // change) mid-job used to leave the job marked Failed in vfps's own UI while Hangfire's
        // own dashboard showed it as Succeeded, because a later, silent re-dispatch of the same
        // job immediately no-op'd against the Failed status this exact scenario used to set - see
        // the catch clause in CsvPseudonymizationJobRunner.RunAsync this now exercises.
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,secret\n");
        FakePseudonymize("ns", "secret", "pseudonym-of-secret");

        // A Hangfire server shutting down cancels ShutdownToken and ThrowIfCancellationRequested()
        // throws as a result - simulated directly here rather than by racing a real shutdown.
        using var shutdownTokenSource = new CancellationTokenSource();
        await shutdownTokenSource.CancelAsync();
        var cancellationToken = A.Fake<IJobCancellationToken>();
        A.CallTo(() => cancellationToken.ShutdownToken).Returns(shutdownTokenSource.Token);
        A.CallTo(() => cancellationToken.ThrowIfCancellationRequested())
            .Throws(() => new OperationCanceledException(shutdownTokenSource.Token));

        var sut = CreateSut();
        var act = () => sut.RunAsync(job.Id, "test-label", cancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();

        A.CallTo(() =>
                jobRepository.UpdateStatusAsync(
                    job.Id,
                    PseudonymizationJobStatus.Failed,
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Theory]
    [InlineData(PseudonymizationJobStatus.Completed)]
    [InlineData(PseudonymizationJobStatus.Failed)]
    [InlineData(PseudonymizationJobStatus.Cancelled)]
    public async Task RunAsync_WithJobAlreadyInTerminalState_ShouldBeNoOp(
        PseudonymizationJobStatus status
    )
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        job.Status = status;
        FakeFindJob(job);

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                jobRepository.UpdateStatusAsync(
                    job.Id,
                    A<PseudonymizationJobStatus>._,
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => s3.GetObjectAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task RunAsync_WithJobStalled_ShouldReprocessAndCompleteInsteadOfNoOp()
    {
        // Unlike Completed/Failed/Cancelled above, Stalled is a heuristic guess by
        // StalledPseudonymizationJobWatchdogService, not a confirmed terminal outcome - a fresh
        // RunAsync call (e.g. Hangfire's own dead-server recovery re-dispatching this job to a
        // live server after its previous runner crashed) must still be able to reprocess it from
        // scratch and reach Completed, or a crashed job would be stuck Stalled forever with no
        // automatic recovery path at all.
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        job.Status = PseudonymizationJobStatus.Stalled;
        FakeFindJob(job);
        // RunAsync re-fetches the job after ProcessAsync finishes to check for a status change
        // that raced with it (see the runner's own comment on that check) - the real repository
        // would report the Running status set below, so the fake must mirror that mutation for
        // this second read to see anything other than the stale Stalled this test starts with.
        A.CallTo(() =>
                jobRepository.UpdateStatusAsync(
                    job.Id,
                    A<PseudonymizationJobStatus>._,
                    A<string?>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(call => job.Status = call.GetArgument<PseudonymizationJobStatus>(1));
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,secret\n");
        FakePseudonymize("ns", "secret", "pseudonym-of-secret");

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                jobRepository.UpdateStatusAsync(
                    job.Id,
                    PseudonymizationJobStatus.Running,
                    null,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._!, 1, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithJobCancelledMidProcessing_ShouldStopEarlyAndNotOverwriteCancelledWithCompleted()
    {
        // ProgressUpdateRowInterval is 200 - 400 rows guarantees a progress/cancellation check
        // fires deterministically (without waiting on the 2-second elapsed-time fallback) right
        // after the row-200 chunk flush, well before all 400 rows are processed.
        const int rowCount = 400;
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        var cancelledJob = new PseudonymizationJob
        {
            Id = job.Id,
            Status = PseudonymizationJobStatus.Cancelled,
            Direction = job.Direction,
            CreatedBy = job.CreatedBy,
            InputObjectKey = job.InputObjectKey,
            ColumnMappings = job.ColumnMappings,
        };
        // Only the very first FindAsync call (the initial status-guard check in RunAsync) should
        // see the original, non-cancelled job - every call after that (the mid-processing check
        // and RunAsync's final "did this get cancelled while we were processing" check) must see
        // the cancelled job, however many of those calls there turn out to be.
        var findCallCount = 0;
        A.CallTo(() => jobRepository.FindAsync(job.Id, A<CancellationToken>._))
            .ReturnsLazily(() => findCallCount++ == 0 ? job : cancelledJob);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));

        var csv = new StringBuilder("id,value\n");
        for (var i = 0; i < rowCount; i++)
        {
            csv.Append(i).Append(',').Append("value").Append(i).Append('\n');
            FakePseudonymize("ns", $"value{i}", $"pseudonym{i}");
        }
        FakeInputObject(job, csv.ToString());

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                jobRepository.CompleteAsync(
                    A<Guid>._,
                    A<string>._,
                    A<long>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        // Processing stopped at the row-200 checkpoint - row 399 must never have been reached.
        A.CallTo(() =>
                pseudonymAppService.CreateTrustedBatchAsync(
                    A<
                        IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)>
                    >.That.Matches(reqs =>
                        reqs.Any(r => r.OriginalValue == $"value{rowCount - 1}")
                    ),
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task RunAsync_WithBatchSizeLargerThanProgressInterval_ShouldStillNoticeCancellationBeforeEndOfFile()
    {
        // PseudonymizeBatchSize (1000 in production) can be much larger than
        // ProgressUpdateRowInterval (200) - this proves the cancellation check isn't tied to the
        // batch/flush boundary. With a 400-row file and a batch size of 1000, nothing would ever
        // flush before EOF if the check only fired on flush - here it must fire independently,
        // at the row-200 mark, before a single batch (let alone the trailing one at EOF) is ever
        // sent, so CreateTrustedBatchAsync must never be called at all.
        const int rowCount = 400;
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        var cancelledJob = new PseudonymizationJob
        {
            Id = job.Id,
            Status = PseudonymizationJobStatus.Cancelled,
            Direction = job.Direction,
            CreatedBy = job.CreatedBy,
            InputObjectKey = job.InputObjectKey,
            ColumnMappings = job.ColumnMappings,
        };
        var findCallCount = 0;
        A.CallTo(() => jobRepository.FindAsync(job.Id, A<CancellationToken>._))
            .ReturnsLazily(() => findCallCount++ == 0 ? job : cancelledJob);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));

        var csv = new StringBuilder("id,value\n");
        for (var i = 0; i < rowCount; i++)
        {
            csv.Append(i).Append(',').Append("value").Append(i).Append('\n');
            FakePseudonymize("ns", $"value{i}", $"pseudonym{i}");
        }
        FakeInputObject(job, csv.ToString());

        var sut = CreateSut(pseudonymizeBatchSize: 1000);
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.CreateTrustedBatchAsync(
                    A<IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)>>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                jobRepository.CompleteAsync(
                    A<Guid>._,
                    A<string>._,
                    A<long>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }
}
