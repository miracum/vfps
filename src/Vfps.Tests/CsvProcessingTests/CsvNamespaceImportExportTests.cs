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

    // A real, non-cancelled job's check-in returns "still active". Without this the fake's own
    // default for a Task<bool> is false, which the runner reads as "cancelled" - every job would
    // then stop dead at its first check-in (row 200), and the cancellation tests below would pass
    // whether or not cancellation actually worked.
    public CsvNamespaceImportExportTests()
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

    private static PseudonymizationJob CreateImportJob(
        string originalColumn = CsvNamespaceColumns.OriginalValue,
        string pseudonymColumn = CsvNamespaceColumns.PseudonymValue,
        bool hasHeaderRow = true,
        // Non-null switches the job to reading each row's target namespace from that column, with
        // Namespace left empty exactly as PseudonymizationJobAppService stores it.
        string? namespaceColumn = null
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
                    Namespace = namespaceColumn is null ? "ns" : string.Empty,
                    NamespaceColumn = namespaceColumn,
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
                    0,
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

        // What a user's Cancel click looks like to a running job: the progress check-in stops
        // matching it. FindAsync is stubbed to the cancelled job alongside that for RunAsync's
        // own final guard, which still re-reads.
        FakeCancelledAfterFirstCheckIn();
        // The first read is RunAsync's own terminal-state guard, which must see a job that is
        // still runnable or nothing is exported at all; every read after it is the final
        // "was this cancelled while we were processing" guard.
        var reads = 0;
        A.CallTo(() => jobRepository.FindAsync(job.Id, A<CancellationToken>._))
            .ReturnsLazily(() =>
                reads++ == 0
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

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() =>
                jobRepository.CompleteAsync(job.Id, A<string>._, A<long>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
        // Not completing is only half of it - RunAsync's final guard would prevent that on its own
        // even if the export had run to the end. What makes this a *midway* stop is that the
        // second page was never fetched: 1200 rows against the exporter's 1000-row page size take
        // two reads to walk, and cancellation lands at the row-200 check-in, inside the first.
        A.CallTo(() =>
                pseudonymRepository.ListByNamespaceAsync(
                    "ns",
                    A<PseudonymPageCursor?>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    // --- namespace column ---------------------------------------------------------------------

    /// <summary>
    /// Captures the (namespace, entries) pairs handed to the import, so a test can assert which
    /// rows were routed where rather than only that something was imported.
    /// </summary>
    private List<(string Namespace, List<PseudonymImportEntry> Entries)> CaptureImportBatches()
    {
        var batches = new List<(string, List<PseudonymImportEntry>)>();
        A.CallTo(() =>
                pseudonymAppService.ImportTrustedBatchAsync(
                    A<Data.Models.Namespace>._,
                    A<IReadOnlyList<PseudonymImportEntry>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(call =>
            {
                var @namespace = call.GetArgument<Data.Models.Namespace>(0)!;
                var entries = call.GetArgument<IReadOnlyList<PseudonymImportEntry>>(1)!;
                batches.Add((@namespace.Name, [.. entries]));

                return Task.FromResult<IReadOnlyList<PseudonymImportResult>>([
                    .. entries.Select(e => new PseudonymImportResult(
                        e,
                        PseudonymImportOutcome.Imported
                    )),
                ]);
            });

        return batches;
    }

    [Fact]
    public async Task RunAsync_WithANamespaceColumn_ShouldRouteEachRowToTheNamespaceItNames()
    {
        var job = CreateImportJob(namespaceColumn: "namespace");
        FakeFindJob(job);
        FakeNamespace("ns-a");
        FakeNamespace("ns-b");
        FakeInputObject(
            job,
            "original,pseudonym,namespace\nalice,psn-1,ns-a\nbob,psn-2,ns-b\ncarol,psn-3,ns-a\n"
        );
        var batches = CaptureImportBatches();

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        // One batch per distinct namespace in the chunk, not one per row.
        batches.Should().HaveCount(2);
        batches
            .Single(b => b.Namespace == "ns-a")
            .Entries.Should()
            .BeEquivalentTo([
                new PseudonymImportEntry("alice", "psn-1"),
                new PseudonymImportEntry("carol", "psn-3"),
            ]);
        batches
            .Single(b => b.Namespace == "ns-b")
            .Entries.Should()
            .BeEquivalentTo([new PseudonymImportEntry("bob", "psn-2")]);
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 3, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithANamespaceColumn_ShouldResolveEachNamespaceOnlyOnce()
    {
        // The same saving the transform path gets from resolving its namespaces up front: a
        // column repeating one name across a million rows must not cost a million lookups.
        var job = CreateImportJob(namespaceColumn: "namespace");
        FakeFindJob(job);
        FakeNamespace("ns-a");
        FakeInputObject(
            job,
            "original,pseudonym,namespace\nalice,psn-1,ns-a\nbob,psn-2,ns-a\ncarol,psn-3,ns-a\n"
        );
        CaptureImportBatches();

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() => namespaceRepository.FindAsync("ns-a", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithANamespaceColumnNamingAnUnknownNamespace_ShouldSkipThatRowOnly()
    {
        // A namespace named by a row is data, not job configuration - one typo must not take a
        // whole file down, unlike a job whose own namespace is missing.
        var job = CreateImportJob(namespaceColumn: "namespace");
        FakeFindJob(job);
        FakeNamespace("ns-a");
        A.CallTo(() => namespaceRepository.FindAsync("no-such-ns", A<CancellationToken>._))
            .Returns((Data.Models.Namespace?)null);
        FakeInputObject(
            job,
            "original,pseudonym,namespace\nalice,psn-1,no-such-ns\nbob,psn-2,ns-a\n"
        );
        var batches = CaptureImportBatches();

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        batches.Should().ContainSingle();
        batches[0].Entries.Should().BeEquivalentTo([new PseudonymImportEntry("bob", "psn-2")]);
        // Both rows still counted and reported - the report lines up with the input row for row.
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 2, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_WithANamespaceColumnLeftBlankOnARow_ShouldSkipThatRowAsMissing()
    {
        // No fallback namespace: quietly putting a row somewhere else is the one outcome this
        // must never produce.
        var job = CreateImportJob(namespaceColumn: "namespace");
        FakeFindJob(job);
        FakeNamespace("ns-a");
        FakeInputObject(job, "original,pseudonym,namespace\nalice,psn-1,\nbob,psn-2,ns-a\n");
        var batches = CaptureImportBatches();

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        batches.Should().ContainSingle();
        batches[0].Entries.Should().BeEquivalentTo([new PseudonymImportEntry("bob", "psn-2")]);
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
            .MustHaveHappened();
    }

    [Fact]
    public async Task RunAsync_WithANamespaceColumn_ShouldNotRequireTheJobsOwnNamespaceToExist()
    {
        // Namespace is stored empty for this shape of job - resolving it up front the way a
        // single-namespace import does would fail every one of them.
        var job = CreateImportJob(namespaceColumn: "namespace");
        FakeFindJob(job);
        FakeNamespace("ns-a");
        FakeInputObject(job, "original,pseudonym,namespace\nalice,psn-1,ns-a\n");
        CaptureImportBatches();

        var sut = CreateSut();
        await sut.RunAsync(job.Id, "test-label", CreateCancellationToken());

        A.CallTo(() => namespaceRepository.FindAsync(string.Empty, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => jobRepository.CompleteAsync(job.Id, A<string>._, 1, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }
}
