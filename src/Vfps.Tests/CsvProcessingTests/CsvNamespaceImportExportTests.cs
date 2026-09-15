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

// The Import/Export counterparts to CsvPseudonymizationJobRunnerTests, and subject to the same
// constraint it documents at length: a fully-faked IAmazonS3 doesn't model TransferUtility
// faithfully enough to assert on the uploaded output object's bytes, so these verify behavior
// through the calls made to the pseudonym app service/repository and the job repository instead.
public class CsvNamespaceImportExportTests
{
    private const string Bucket = "test-bucket";

    private readonly IPseudonymizationJobRepository jobRepository =
        A.Fake<IPseudonymizationJobRepository>();
    private readonly IPseudonymAppService pseudonymAppService = A.Fake<IPseudonymAppService>();
    private readonly INamespaceRepository namespaceRepository = A.Fake<INamespaceRepository>();
    private readonly IPseudonymRepository pseudonymRepository = A.Fake<IPseudonymRepository>();
    private readonly IAmazonS3 s3 = A.Fake<IAmazonS3>();

    // See CsvPseudonymizationJobRunnerTests for why JobCancellationToken.Null can't be used here.
    private static IJobCancellationToken CreateCancellationToken()
    {
        var token = A.Fake<IJobCancellationToken>();
        A.CallTo(() => token.ShutdownToken).Returns(CancellationToken.None);
        return token;
    }

    private CsvPseudonymizationJobRunner CreateSut(List<string>? missingValuePlaceholders = null)
    {
        var s3Config = Options.Create(new S3Config { Bucket = Bucket });
        var csvProcessingConfig = Options.Create(
            new CsvProcessingConfig
            {
                MissingValuePlaceholders = missingValuePlaceholders ?? ["NA", "NULL"],
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
                outputUploader
            ),
            new CsvNamespaceImporter(
                pseudonymAppService,
                namespaceRepository,
                s3,
                s3Config,
                csvProcessingConfig,
                outputUploader
            ),
            new CsvNamespaceExporter(pseudonymRepository, namespaceRepository, outputUploader),
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

    private static PseudonymizationJob CreateImportJob(
        string originalColumn = CsvNamespaceColumns.OriginalValue,
        string pseudonymColumn = CsvNamespaceColumns.PseudonymValue,
        bool hasHeaderRow = true
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            Status = PseudonymizationJobStatus.Queued,
            Direction = PseudonymizationJobDirection.Import,
            CreatedBy = "test-user",
            InputObjectKey = "csv-jobs/input.csv",
            HasHeaderRow = hasHeaderRow,
            ColumnMappings =
            [
                new ColumnMapping
                {
                    SourceColumn = originalColumn,
                    TargetColumn = pseudonymColumn,
                    Namespace = "ns",
                },
            ],
        };

    private static PseudonymizationJob CreateExportJob() =>
        new()
        {
            Id = Guid.NewGuid(),
            Status = PseudonymizationJobStatus.Queued,
            Direction = PseudonymizationJobDirection.Export,
            CreatedBy = "test-user",
            // Deliberately null, exactly as PseudonymizationJobAppService.CreateExportJobAsync
            // creates it: an export has no input object at all.
            InputObjectKey = null,
            ColumnMappings =
            [
                new ColumnMapping
                {
                    SourceColumn = CsvNamespaceColumns.OriginalValue,
                    TargetColumn = CsvNamespaceColumns.PseudonymValue,
                    Namespace = "ns",
                },
            ],
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

    private void FakeNamespace(string name) =>
        A.CallTo(() => namespaceRepository.FindAsync(name, A<CancellationToken>._))
            .Returns(CreateNamespace(name));

    // Every entry handed to the import is echoed back as Imported, so these tests can assert on
    // what was *offered* for import without also re-testing the app service's own classification
    // (PseudonymAppServiceTests covers that against a real database).
    private void FakeImportAcceptsEverything() =>
        A.CallTo(() =>
                pseudonymAppService.ImportTrustedBatchAsync(
                    A<Data.Models.Namespace>._,
                    A<IReadOnlyList<PseudonymImportEntry>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(call =>
                Task.FromResult<IReadOnlyList<PseudonymImportResult>>([
                    .. call.GetArgument<IReadOnlyList<PseudonymImportEntry>>(1)!
                        .Select(e => new PseudonymImportResult(e, PseudonymImportOutcome.Imported)),
                ])
            );

    [Fact]
    public async Task RunAsync_WithImportDirection_ShouldImportEachRowsPairAndComplete()
    {
        var job = CreateImportJob();
        FakeFindJob(job);
        FakeNamespace("ns");
        FakeInputObject(job, "original,pseudonym\nalice,psn-1\nbob,psn-2\n");
        FakeImportAcceptsEverything();

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.ImportTrustedBatchAsync(
                    A<Data.Models.Namespace>.That.Matches(ns => ns.Name == "ns"),
                    A<IReadOnlyList<PseudonymImportEntry>>.That.IsSameSequenceAs(
                        new PseudonymImportEntry("alice", "psn-1"),
                        new PseudonymImportEntry("bob", "psn-2")
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 2, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithImportDirection_ShouldNeverGeneratePseudonyms()
    {
        // The whole point of an import: the pseudonym comes from the file, so none of the
        // generating paths may be touched no matter what the file contains.
        var job = CreateImportJob();
        FakeFindJob(job);
        FakeNamespace("ns");
        FakeInputObject(job, "original,pseudonym\nalice,psn-1\n");
        FakeImportAcceptsEverything();

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.CreateTrustedBatchAsync(
                    A<IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)>>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                pseudonymAppService.CreateTrustedAsync(
                    A<Data.Models.Namespace>._,
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task RunAsync_WithImportDirection_ShouldResolveColumnsByTheJobsOwnNames()
    {
        var job = CreateImportJob(originalColumn: "mrn", pseudonymColumn: "psn");
        FakeFindJob(job);
        FakeNamespace("ns");
        FakeInputObject(job, "psn,unrelated,mrn\npsn-1,ignored,alice\n");
        FakeImportAcceptsEverything();

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.ImportTrustedBatchAsync(
                    A<Data.Models.Namespace>._,
                    A<IReadOnlyList<PseudonymImportEntry>>.That.IsSameSequenceAs(
                        new PseudonymImportEntry("alice", "psn-1")
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithImportDirectionAndNoHeaderRow_ShouldResolveColumnsByIndex()
    {
        var job = CreateImportJob(originalColumn: "1", pseudonymColumn: "0", hasHeaderRow: false);
        FakeFindJob(job);
        FakeNamespace("ns");
        FakeInputObject(job, "psn-1,alice\n");
        FakeImportAcceptsEverything();

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.ImportTrustedBatchAsync(
                    A<Data.Models.Namespace>._,
                    A<IReadOnlyList<PseudonymImportEntry>>.That.IsSameSequenceAs(
                        new PseudonymImportEntry("alice", "psn-1")
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData("original,pseudonym\n,psn-1\nbob,psn-2\n")]
    [InlineData("original,pseudonym\nalice,\nbob,psn-2\n")]
    [InlineData("original,pseudonym\nNA,psn-1\nbob,psn-2\n")]
    [InlineData("original,pseudonym\nalice,NULL\nbob,psn-2\n")]
    public async Task RunAsync_WithImportDirectionAndAMissingValue_ShouldSkipThatRowButStillReportIt(
        string csvContent
    )
    {
        // A blank or placeholder cell on either side isn't importable, but the row still has to
        // appear in the report and be counted - the same "pass it through and count it" handling
        // every other direction gives a missing value.
        var job = CreateImportJob();
        FakeFindJob(job);
        FakeNamespace("ns");
        FakeInputObject(job, csvContent);
        FakeImportAcceptsEverything();

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymAppService.ImportTrustedBatchAsync(
                    A<Data.Models.Namespace>._,
                    A<IReadOnlyList<PseudonymImportEntry>>.That.IsSameSequenceAs(
                        new PseudonymImportEntry("bob", "psn-2")
                    ),
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
            .MustHaveHappened();
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 2, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithImportDirectionAndUnknownNamespace_ShouldFailTheJob()
    {
        var job = CreateImportJob();
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns((Data.Models.Namespace?)null);
        FakeInputObject(job, "original,pseudonym\nalice,psn-1\n");

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

    private void FakeNamespaceContents(string namespaceName, int count)
    {
        var all = Enumerable
            .Range(0, count)
            .Select(i => new Data.Models.Pseudonym
            {
                NamespaceName = namespaceName,
                OriginalValue = $"o{i}",
                PseudonymValue = $"p{i}",
                // Descending, so the keyset order the repository promises (created_at DESC) is
                // also this list's own order and paging by cursor can be faked by index.
                CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(count - i),
            })
            .ToList();

        A.CallTo(() =>
                pseudonymRepository.ListByNamespaceAsync(
                    namespaceName,
                    A<PseudonymPageCursor?>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(call =>
            {
                var cursor = call.GetArgument<PseudonymPageCursor?>(1);
                var pageSize = call.GetArgument<int>(2);
                var skip = cursor is null
                    ? 0
                    : all.FindIndex(p => p.OriginalValue == cursor.OriginalValue) + 1;

                return Task.FromResult<IReadOnlyList<Data.Models.Pseudonym>>([
                    .. all.Skip(skip).Take(pageSize),
                ]);
            });
    }

    [Fact]
    public async Task RunAsync_WithExportDirection_ShouldWriteEveryPseudonymAndComplete()
    {
        var job = CreateExportJob();
        FakeFindJob(job);
        FakeNamespace("ns");
        FakeNamespaceContents("ns", 3);

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 3, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithExportDirection_ShouldPageThroughTheWholeNamespace()
    {
        // 1200 rows against the exporter's own 1000-row page size: a full page followed by a
        // partial one, which is exactly the boundary the keyset cursor has to get right.
        var job = CreateExportJob();
        FakeFindJob(job);
        FakeNamespace("ns");
        FakeNamespaceContents("ns", 1200);

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                pseudonymRepository.ListByNamespaceAsync(
                    "ns",
                    A<PseudonymPageCursor?>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedTwiceExactly();
        A.CallTo(() =>
                jobRepository.CompleteAsync(job.Id, A<string>._, 1200, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithExportDirection_ShouldNotReadAnyInputObject()
    {
        var job = CreateExportJob();
        FakeFindJob(job);
        FakeNamespace("ns");
        FakeNamespaceContents("ns", 2);

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() => s3.GetObjectAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task RunAsync_WithExportDirectionAndEmptyNamespace_ShouldCompleteWithNoRows()
    {
        var job = CreateExportJob();
        FakeFindJob(job);
        FakeNamespace("ns");
        FakeNamespaceContents("ns", 0);

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 0, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithExportDirectionAndUnknownNamespace_ShouldFailTheJob()
    {
        var job = CreateExportJob();
        FakeFindJob(job);
        A.CallTo(() => namespaceRepository.FindAsync("ns", A<CancellationToken>._))
            .Returns((Data.Models.Namespace?)null);

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
    public async Task RunAsync_WithExportDirectionCancelledMidway_ShouldStopWithoutCompleting()
    {
        var job = CreateExportJob();
        FakeNamespace("ns");
        FakeNamespaceContents("ns", 1200);

        // The runner's own progress check-in re-reads the job; hand it back as Cancelled from the
        // second read onwards, which is what a user's Cancel click looks like to a running job.
        var reads = 0;
        A.CallTo(() => jobRepository.FindAsync(job.Id, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                reads++;
                return Task.FromResult<PseudonymizationJob?>(
                    reads == 1
                        ? job
                        : new PseudonymizationJob
                        {
                            Id = job.Id,
                            CreatedBy = job.CreatedBy,
                            Direction = job.Direction,
                            ColumnMappings = job.ColumnMappings,
                            Status = PseudonymizationJobStatus.Cancelled,
                        }
                );
            });

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                jobRepository.CompleteAsync(job.Id, A<string>._, A<long>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }
}
