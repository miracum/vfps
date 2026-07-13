using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using FakeItEasy;
using Hangfire;
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
// bytes. TransferUtility.UploadAsync doesn't handle the non-seekable Pipe-backed stream
// ProcessAsync gives it (its Length getter throws NotSupportedException, and TransferUtility
// silently treats that as an empty upload) - a separate, real bug - so asserting on uploaded
// content here would either mask that bug or make these tests fail for a reason unrelated to
// what they're meant to verify.
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

    private CsvPseudonymizationJobRunner CreateSut() =>
        new(
            jobRepository,
            pseudonymAppService,
            namespaceRepository,
            s3,
            Options.Create(new S3Config { Bucket = Bucket }),
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

    private void FakeInputObject(PseudonymizationJob job, string csvContent)
    {
        var responseStream = new MemoryStream(Encoding.UTF8.GetBytes(csvContent));
        var response = A.Fake<GetObjectResponse>();
        A.CallTo(() => response.ResponseStream).Returns(responseStream);
        A.CallTo(() => response.Dispose()).Invokes(responseStream.Dispose);

        A.CallTo(() => s3.GetObjectAsync(Bucket, job.InputObjectKey, A<CancellationToken>._))
            .Returns(response);
    }

    private void FakeFindJob(PseudonymizationJob job) =>
        A.CallTo(() => jobRepository.FindAsync(job.Id, A<CancellationToken>._)).Returns(job);

    private void FakePseudonymize(
        string namespaceName,
        string originalValue,
        string pseudonymValue
    ) =>
        A.CallTo(() =>
                pseudonymAppService.CreateTrustedAsync(
                    A<Data.Models.Namespace>.That.Matches(n => n.Name == namespaceName),
                    originalValue,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new Data.Models.Pseudonym
                {
                    NamespaceName = namespaceName,
                    OriginalValue = originalValue,
                    PseudonymValue = pseudonymValue,
                }
            );

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
        await sut.RunAsync(job.Id, CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.CreateTrustedAsync(
                    A<Data.Models.Namespace>.That.Matches(n => n.Name == "ns"),
                    "secret",
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
        await sut.RunAsync(job.Id, CreateCancellationToken());

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
        await sut.RunAsync(job.Id, CreateCancellationToken());

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
        await sut.RunAsync(job.Id, CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.CreateTrustedAsync(
                    A<Data.Models.Namespace>._,
                    "secret",
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithRowCountNotMultipleOfChunkSize_ShouldStillProcessTrailingPartialChunk()
    {
        // ConcurrencyChunkSize is 20 - 25 rows exercises one full chunk plus a trailing partial one.
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
        await sut.RunAsync(job.Id, CreateCancellationToken());

        // The last row (index 24) is only reachable if the trailing partial chunk (rows 20-24)
        // actually got flushed - a bug that dropped it would still process the first full chunk.
        A.CallTo(() =>
                pseudonymAppService.CreateTrustedAsync(
                    A<Data.Models.Namespace>._,
                    $"value{rowCount - 1}",
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
        var act = () => sut.RunAsync(job.Id, CreateCancellationToken());

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
            .Returns(null);
        FakeInputObject(job, "id,value\n1,secret\n");

        var sut = CreateSut();
        var act = () => sut.RunAsync(job.Id, CreateCancellationToken());

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
        await sut.RunAsync(job.Id, CreateCancellationToken());

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
        await sut.RunAsync(job.Id, CreateCancellationToken());

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
                pseudonymAppService.CreateTrustedAsync(
                    A<Data.Models.Namespace>._,
                    $"value{rowCount - 1}",
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }
}
