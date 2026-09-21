using System.Diagnostics;
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
    private readonly IPseudonymRepository pseudonymRepository = A.Fake<IPseudonymRepository>();
    private readonly IAmazonS3 s3 = A.Fake<IAmazonS3>();

    // A real, non-cancelled job's check-in returns "still active". Without this the fake's own
    // default for a Task<bool> is false, which the runner reads as "cancelled" - every job would
    // then stop dead at its first check-in (row 200), and the cancellation tests below would pass
    // whether or not cancellation actually worked.
    public CsvPseudonymizationJobRunnerTests()
    {
        A.CallTo(() =>
                jobRepository.UpdateProgressUnlessCancelledAsync(
                    A<Guid>._,
                    A<long>._,
                    A<long>._,
                    A<int>._,
                    A<int>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .Returns(true);
    }

    // Drives a cancellation the way the runner actually notices one: the progress check-in's
    // single round trip declines to match a cancelled job (see
    // IPseudonymizationJobRepository.UpdateProgressUnlessCancelledAsync). RunAsync's own final
    // "was this cancelled while processing" guard still re-reads the job, so callers stub
    // FindAsync for that part separately.
    private void FakeCancelledAfterFirstCheckIn()
    {
        var checkIns = 0;
        A.CallTo(() =>
                jobRepository.UpdateProgressUnlessCancelledAsync(
                    A<Guid>._,
                    A<long>._,
                    A<long>._,
                    A<int>._,
                    A<int>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() => checkIns++ > 0);
    }

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
    // The per-direction processors are wired up for real rather than faked: they are the code
    // under test here (the runner itself only dispatches to them), and they take the same faked
    // S3/repository/app-service dependencies this class already sets up.
    private CsvPseudonymizationJobRunner CreateSut(
        int pseudonymizeBatchSize = 20,
        List<string>? missingValuePlaceholders = null
    )
    {
        var s3Config = Options.Create(new S3Config { Bucket = Bucket });
        var csvProcessingConfig = Options.Create(
            new CsvProcessingConfig
            {
                PseudonymizeBatchSize = pseudonymizeBatchSize,
                MissingValuePlaceholders = missingValuePlaceholders ?? ["NA", "NULL"],
                // Zero, so the progress/cancellation gate below falls back to its row interval
                // alone: these tests process a handful of rows in milliseconds, and the production
                // default (seconds) would mean a job never checks in - or notices a cancellation -
                // before reaching the end of the file.
                ProgressUpdateInterval = TimeSpan.Zero,
            }
        );
        var outputUploader = new CsvJobOutputUploader(s3, s3Config);

        return new(
            jobRepository,
            new CsvColumnTransformer(
                pseudonymAppService,
                namespaceRepository,
                s3,
                s3Config,
                csvProcessingConfig,
                outputUploader,
                NullLogger<CsvColumnTransformer>.Instance
            ),
            new CsvNamespaceImporter(
                pseudonymAppService,
                namespaceRepository,
                s3,
                s3Config,
                csvProcessingConfig,
                outputUploader,
                NullLogger<CsvNamespaceImporter>.Instance
            ),
            new CsvNamespaceExporter(pseudonymRepository, namespaceRepository, outputUploader),
            csvProcessingConfig,
            NullLogger<CsvPseudonymizationJobRunner>.Instance
        );
    }

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
    ) => CreateJob(direction, PseudonymizeMode.CreateIfMissing, columnMappings);

    private static PseudonymizationJob CreateJob(
        PseudonymizationJobDirection direction,
        PseudonymizeMode pseudonymizeMode,
        params ColumnMapping[] columnMappings
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            Status = PseudonymizationJobStatus.Queued,
            Direction = direction,
            PseudonymizeMode = pseudonymizeMode,
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
    // per value (see FlushChunkAsync), so FakePseudonymize registers known
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

    // Same shape as FakePseudonymize above, and for the same reason: the Depseudonymize path now
    // resolves a whole chunk through one ReverseLookupTrustedBatchAsync call rather than one
    // ReverseLookupTrustedAsync per value. A value registered with a null originalValue stands for
    // an unknown pseudonym and is simply left out of the returned dictionary, which is how the
    // batch contract expresses a miss.
    private readonly Dictionary<
        (string Namespace, string PseudonymValue),
        string?
    > knownOriginalValues = [];
    private bool batchDepseudonymizeFakeConfigured;

    private void FakeDepseudonymize(
        string namespaceName,
        string pseudonymValue,
        string? originalValue
    )
    {
        knownOriginalValues[(namespaceName, pseudonymValue)] = originalValue;

        if (batchDepseudonymizeFakeConfigured)
        {
            return;
        }

        batchDepseudonymizeFakeConfigured = true;
        A.CallTo(() =>
                pseudonymAppService.ReverseLookupTrustedBatchAsync(
                    A<IReadOnlyList<(Data.Models.Namespace Namespace, string PseudonymValue)>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(call =>
            {
                var requests = call.GetArgument<
                    IReadOnlyList<(Data.Models.Namespace Namespace, string PseudonymValue)>
                >(0)!;

                var result = new Dictionary<(string, string), Data.Models.Pseudonym>();
                foreach (var (ns, pseudonymValue) in requests)
                {
                    var key = (ns.Name, pseudonymValue);
                    if (
                        !knownOriginalValues.TryGetValue(key, out var originalValue)
                        || originalValue is null
                    )
                    {
                        continue;
                    }

                    result[key] = new Data.Models.Pseudonym
                    {
                        NamespaceName = ns.Name,
                        OriginalValue = originalValue,
                        PseudonymValue = pseudonymValue,
                    };
                }

                return Task.FromResult(
                    (IReadOnlyDictionary<(string, string), Data.Models.Pseudonym>)result
                );
            });
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

    // The lookup-only counterpart to FakePseudonymize: only the registered pairs resolve, and
    // anything else is simply absent from the returned dictionary - which is exactly how
    // ResolveTrustedBatchAsync reports "this namespace has never seen that value".
    private readonly Dictionary<
        (string Namespace, string OriginalValue),
        string
    > existingPseudonymValues = [];
    private bool batchResolveFakeConfigured;

    private void FakeExistingPseudonym(
        string namespaceName,
        string originalValue,
        string pseudonymValue
    )
    {
        existingPseudonymValues[(namespaceName, originalValue)] = pseudonymValue;

        if (batchResolveFakeConfigured)
        {
            return;
        }

        batchResolveFakeConfigured = true;
        A.CallTo(() =>
                pseudonymAppService.ResolveTrustedBatchAsync(
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
                    if (existingPseudonymValues.TryGetValue(key, out var pseudonymValue))
                    {
                        result[key] = new Data.Models.Pseudonym
                        {
                            NamespaceName = ns.Name,
                            OriginalValue = originalValue,
                            PseudonymValue = pseudonymValue,
                        };
                    }
                }

                return Task.FromResult(
                    (IReadOnlyDictionary<(string, string), Data.Models.Pseudonym>)result
                );
            });
    }

    [Theory]
    [InlineData(PseudonymizeMode.FailIfMissing)]
    [InlineData(PseudonymizeMode.KeepIfMissing)]
    public async Task RunAsync_WithALookupOnlyMode_ShouldResolveWithoutEverCreating(
        PseudonymizeMode mode
    )
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            mode,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,known\n");
        FakeExistingPseudonym("ns", "known", "pseudonym-of-known");

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.ResolveTrustedBatchAsync(
                    A<
                        IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)>
                    >.That.Matches(reqs =>
                        reqs.Any(r => r.Namespace.Name == "ns" && r.OriginalValue == "known")
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();

        // The whole point of the mode: nothing is minted, whatever the file contains.
        A.CallTo(() =>
                pseudonymAppService.CreateTrustedBatchAsync(
                    A<IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)>>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 1, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithFailIfMissing_AndAnUnknownValue_ShouldFailWithoutCompleting()
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            PseudonymizeMode.FailIfMissing,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,known\n2,never-seen\n");
        FakeExistingPseudonym("ns", "known", "pseudonym-of-known");

        var sut = CreateSut();
        var run = () => sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        await run.Should().ThrowAsync<UnresolvedOriginalValueException>();

        A.CallTo(() =>
                jobRepository.CompleteAsync(job.Id, A<string>._, A<long>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task RunAsync_WithFailIfMissing_ShouldRecordWhereItStoppedButNeverTheValue()
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            PseudonymizeMode.FailIfMissing,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,known\n2,never-seen\n");
        FakeExistingPseudonym("ns", "known", "pseudonym-of-known");

        var sut = CreateSut();
        var run = () => sut.RunAsync(job.Id, "test-label", CreateCancellationToken());
        await run.Should().ThrowAsync<UnresolvedOriginalValueException>();

        // Unlike every other CSV failure, this one's own message is persisted rather than replaced
        // with the generic "see server logs" text - so what it may and may not contain is the
        // thing worth pinning down. The row, the column and the namespace are the caller's own job
        // configuration coming back; the value never appears.
        A.CallTo(() =>
                jobRepository.UpdateStatusAsync(
                    job.Id,
                    PseudonymizationJobStatus.Failed,
                    A<string>.That.Matches(message =>
                        message!.Contains("data row 2", StringComparison.Ordinal)
                        && message.Contains("'ns'", StringComparison.Ordinal)
                        && message.Contains("'value'", StringComparison.Ordinal)
                        && !message.Contains("never-seen", StringComparison.Ordinal)
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithKeepIfMissing_ShouldCountUnknownValuesAndComplete()
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            PseudonymizeMode.KeepIfMissing,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,known\n2,never-seen\n3,also-never-seen\n");
        FakeExistingPseudonym("ns", "known", "pseudonym-of-known");

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        // Three rows written, two of them with their original value kept as-is - counted as
        // unresolved (the sixth argument) rather than as missing input values (the fifth), which
        // they are not.
        A.CallTo(() =>
                jobRepository.UpdateProgressAsync(
                    job.Id,
                    A<long>._,
                    3,
                    0,
                    0,
                    2,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappened();
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 3, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithKeepIfMissing_ShouldCountABlankInputCellAsMissingNotUnresolved()
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            PseudonymizeMode.KeepIfMissing,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        // Row 2's cell is blank and row 3's is a configured placeholder: neither is a value this
        // namespace has never seen, they are values that were never there to resolve.
        FakeInputObject(job, "id,value\n1,known\n2,\n3,NA\n");
        FakeExistingPseudonym("ns", "known", "pseudonym-of-known");

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                jobRepository.UpdateProgressAsync(
                    job.Id,
                    A<long>._,
                    3,
                    0,
                    2,
                    0,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappened();
    }

    [Fact]
    public async Task RunAsync_WithFailIfMissing_ShouldNotFailOverABlankInputCell()
    {
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            PseudonymizeMode.FailIfMissing,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,known\n2,\n");
        FakeExistingPseudonym("ns", "known", "pseudonym-of-known");

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        // A blank cell is what a real-world CSV export routinely contains, and it is already a
        // tolerated, counted case in every other mode - failing a job over one would make this
        // mode unusable on real files for a reason that has nothing to do with what it guards.
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 2, A<CancellationToken>._))
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
                    A<string>.That.Contains(job.InputObjectKey!)
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
                    0,
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
                    0,
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
                pseudonymAppService.ReverseLookupTrustedBatchAsync(
                    A<
                        IReadOnlyList<(Data.Models.Namespace Namespace, string PseudonymValue)>
                    >.That.Matches(requests =>
                        requests.Any(r =>
                            r.Namespace.Name == "ns" && r.PseudonymValue == "pseudonym-of-secret"
                        )
                    ),
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
        // The transformer falls back to the raw value when a pseudonym is absent from the batch
        // result (see CsvColumnTransformer's Resolve local function) rather than failing the row -
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
        // fallback as an unmatched pseudonym), so this couldn't crash the
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
                pseudonymAppService.ReverseLookupTrustedBatchAsync(
                    A<
                        IReadOnlyList<(Data.Models.Namespace Namespace, string PseudonymValue)>
                    >.That.Matches(requests =>
                        requests.Any(r =>
                            r.Namespace.Name == "ns" && r.PseudonymValue == "pseudonym-of-secret"
                        )
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        // The blank/"NA"/"null" rows must never reach the batch at all - there is nothing to look
        // up for them, and including them would spend a lookup slot on a guaranteed miss.
        A.CallTo(() =>
                pseudonymAppService.ReverseLookupTrustedBatchAsync(
                    A<
                        IReadOnlyList<(Data.Models.Namespace Namespace, string PseudonymValue)>
                    >.That.Matches(requests =>
                        requests.Any(r =>
                            r.PseudonymValue.Length == 0
                            || r.PseudonymValue == "NA"
                            || r.PseudonymValue == "null"
                        )
                    ),
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
                    0,
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

    [Fact]
    public async Task RunAsync_WhenHangfireReassignsTheFetch_ShouldNotMarkJobFailed()
    {
        // JobAbortedException is Hangfire's own signal that this job's queue entry no longer
        // names this server/worker as the one holding it - most commonly its invisibility-timeout
        // fetch lease expiring while this execution was still genuinely running, handing the same
        // job to a second worker. It is a genuine Hangfire.Server.JobAbortedException (itself an
        // OperationCanceledException) here, not a hand-rolled one, so this also proves the runner
        // catches it by its real type rather than by some looser shape that happened to work.
        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns(CreateNamespace("ns"));
        FakeInputObject(job, "id,value\n1,secret\n");
        FakePseudonymize("ns", "secret", "pseudonym-of-secret");

        // Not shutting down - a reassigned fetch throws this even while ShutdownToken is
        // perfectly healthy, which is exactly what makes it a distinct case from the one above
        // rather than a special case of it.
        var cancellationToken = A.Fake<IJobCancellationToken>();
        A.CallTo(() => cancellationToken.ShutdownToken).Returns(CancellationToken.None);
        A.CallTo(() => cancellationToken.ThrowIfCancellationRequested())
            .Throws(() => new JobAbortedException());

        var sut = CreateSut();
        var act = () => sut.RunAsync(job.Id, "test-label", cancellationToken);

        await act.Should().ThrowAsync<JobAbortedException>();

        // Same outcome as a real shutdown: something else now owns this job, so this execution
        // must not mark it Failed and strand whichever execution actually finishes it behind a
        // terminal status.
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
    public async Task RunAsync_ShouldRunTheWholeJobInsideOneSpanCarryingItsPhaseBreakdown()
    {
        // The reason this span exists at all: a Hangfire job has no ambient Activity of its own,
        // so without it every database span the job produces is a parentless root trace rather
        // than a child of one readable job timeline.
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Vfps",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

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

        var jobActivity = activities
            .Should()
            .ContainSingle(a => a.OperationName == "CsvPseudonymizationJob")
            .Subject;
        jobActivity.GetTagItem("vfps.job.id").Should().Be(job.Id);
        jobActivity.GetTagItem("vfps.job.direction").Should().Be("Pseudonymize");
        jobActivity.GetTagItem("vfps.job.rows_processed").Should().Be(1L);
        jobActivity.GetTagItem("vfps.csv.phase.parse_input.seconds").Should().NotBeNull();
        jobActivity.GetTagItem("vfps.csv.phase.resolve_database.seconds").Should().NotBeNull();
        jobActivity.GetTagItem("vfps.csv.phase.write_output.seconds").Should().NotBeNull();
        jobActivity.GetTagItem("vfps.csv.phase.report_progress.seconds").Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_WithFailingJob_ShouldStillRecordTheSpanAndItsPhaseBreakdown()
    {
        // A job that died partway through is when the breakdown is worth most, so it is published
        // from a finally rather than only on the success path.
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Vfps",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var job = CreateJob(
            PseudonymizationJobDirection.Pseudonymize,
            new ColumnMapping { SourceColumn = "value", Namespace = "missing-ns" }
        );
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("missing-ns", A<CancellationToken>._))
            .Returns<Data.Models.Namespace?>(null);
        FakeInputObject(job, "id,value\n1,secret\n");

        var sut = CreateSut();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunAsync(job.Id, "test-label", CreateCancellationToken())
        );

        var jobActivity = activities
            .Should()
            .ContainSingle(a => a.OperationName == "CsvPseudonymizationJob")
            .Subject;
        jobActivity.Status.Should().Be(ActivityStatusCode.Error);
        // The exception's type, never its message - a raw exception string can carry the row
        // content this service exists to protect.
        jobActivity.StatusDescription.Should().Be(nameof(InvalidOperationException));
        jobActivity.GetTagItem("vfps.csv.phase.parse_input.seconds").Should().NotBeNull();
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
        // The mid-processing cancellation is delivered through the progress check-in itself;
        // FindAsync is stubbed alongside it purely for RunAsync's final "did this get cancelled
        // while we were processing" guard, which still re-reads the job.
        FakeCancelledAfterFirstCheckIn();
        // The first read is RunAsync's own terminal-state guard, which must see a job that is
        // still runnable or nothing is processed at all; every read after it is the final
        // "was this cancelled while we were processing" guard.
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
        FakeCancelledAfterFirstCheckIn();
        // The first read is RunAsync's own terminal-state guard, which must see a job that is
        // still runnable or nothing is processed at all; every read after it is the final
        // "was this cancelled while we were processing" guard.
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
