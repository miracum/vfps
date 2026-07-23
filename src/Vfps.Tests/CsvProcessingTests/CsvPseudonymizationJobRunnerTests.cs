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

// These tests deliberately verify the row-transform logic (which namespace/value each field
// resolves to, in what order, and how many rows get processed) via the calls made to
// pseudonymAppService/jobRepository, rather than by reading back the uploaded output object's
// bytes. A fully-faked IAmazonS3 doesn't model TransferUtility faithfully enough for that:
// TransferUtility.UploadAsync reads internal client config (e.g. buffer/part size) off
// IAmazonS3.Config, and FakeItEasy auto-fakes that property to an object with zeroed-out
// values, which makes TransferUtility produce a bogus empty upload - confirmed to be a test-only
// artifact, not a real bug, via an actual end-to-end run (real browser upload, real MinIO,
// correct pseudonymized content downloaded back). Asserting on "uploaded" content here would
// just be asserting on that artifact.
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
    private CsvPseudonymizationJobRunner CreateSut(int pseudonymizeBatchSize = 20) =>
        new(
            jobRepository,
            pseudonymAppService,
            namespaceRepository,
            s3,
            Options.Create(new S3Config { Bucket = Bucket }),
            Options.Create(
                new CsvProcessingConfig { PseudonymizeBatchSize = pseudonymizeBatchSize }
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
                jobRepository.UpdateProgressAsync(job.Id, A<long>._, 1, 1, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 1, A<CancellationToken>._))
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
    [InlineData(PseudonymizationJobStatus.Stalled)]
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
