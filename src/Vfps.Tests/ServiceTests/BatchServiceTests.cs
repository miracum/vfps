using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using FakeItEasy;
using Grpc.Core;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.Data.Models;
using BatchService = Vfps.Services.BatchService;

namespace Vfps.Tests.ServiceTests;

public class BatchServiceTests : ServiceTestBase
{
    private const string Bucket = "test-bucket";

    private readonly PseudonymizationJobRepository repository;
    private readonly IAmazonS3 s3;
    private readonly IBackgroundJobClient backgroundJobClient;

    public BatchServiceTests()
    {
        repository = new PseudonymizationJobRepository(ContextFactory);

        s3 = A.Fake<IAmazonS3>();
        A.CallTo(() => s3.GetPreSignedURLAsync(A<GetPreSignedUrlRequest>._))
            .Returns("https://example.invalid/presigned");
        A.CallTo(() => s3.GetObjectMetadataAsync(Bucket, A<string>._, A<CancellationToken>._))
            .Returns(new GetObjectMetadataResponse { ContentLength = 1234 });

        backgroundJobClient = A.Fake<IBackgroundJobClient>();
        A.CallTo(() => backgroundJobClient.Create(A<Job>._, A<IState>._))
            .Returns("hangfire-job-id");
    }

    private BatchService CreateSut(AuthorizationConfig? config = null) =>
        new(
            new PseudonymizationJobAppService(
                repository,
                CreatePermissionChecker(config),
                s3,
                backgroundJobClient,
                Options.Create(new S3Config { Bucket = Bucket })
            )
        );

    private static ServerCallContext Context() =>
        TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken);

    private static BatchServiceCreateRequest PseudonymizeRequest() =>
        new()
        {
            OriginalFileName = "patients.csv",
            Transform = new BatchTransformSpec
            {
                Direction = BatchTransformDirection.Pseudonymize,
                ColumnMappings =
                {
                    new BatchColumnMapping
                    {
                        SourceColumn = "patient_id",
                        Namespace = "existingNamespace",
                    },
                },
            },
        };

    private void InputNotUploadedYet() =>
        A.CallTo(() => s3.GetObjectMetadataAsync(Bucket, A<string>._, A<CancellationToken>._))
            .Throws(new AmazonS3Exception("Not Found") { StatusCode = HttpStatusCode.NotFound });

    [Fact]
    public async Task Create_WithATransform_ShouldReturnTheBatchAwaitingItsUpload()
    {
        var sut = CreateSut();

        var response = await sut.Create(PseudonymizeRequest(), Context());

        response.Batch.State.Should().Be(BatchState.AwaitingUpload);
        response.Batch.OriginalFileName.Should().Be("patients.csv");
        response.Batch.Transform.Direction.Should().Be(BatchTransformDirection.Pseudonymize);
        response
            .Batch.Transform.PseudonymizeMode.Should()
            .Be(BatchPseudonymizeMode.CreateIfMissing);
        response.Batch.Transform.Format.Encoding.Should().Be(CsvEncoding.Utf8);
        response.Batch.Transform.Format.Delimiter.Should().Be(",");
        response.Batch.Transform.Format.HasHeaderRow.Should().BeTrue();
        response
            .Batch.Transform.ColumnMappings.Should()
            .ContainSingle()
            .Which.Namespace.Should()
            .Be("existingNamespace");
    }

    [Fact]
    public async Task Create_WithATransform_ShouldReturnTheUploadTargetWithItsSignedHeaders()
    {
        // The Content-Type is part of the upload URL's signature: a client that doesn't send it
        // exactly is rejected by S3, so the response has to tell it.
        var sut = CreateSut();

        var response = await sut.Create(PseudonymizeRequest(), Context());

        response.Upload.Method.Should().Be("PUT");
        response.Upload.Uri.Should().Be("https://example.invalid/presigned");
        response.Upload.Headers.Should().Contain("Content-Type", "text/csv");
        response.Upload.ExpiresAt.ToDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Create_WithAnExport_ShouldQueueItWithoutAnUploadTarget()
    {
        var sut = CreateSut();

        var response = await sut.Create(
            new BatchServiceCreateRequest
            {
                Export = new BatchExportSpec { Namespace = "existingNamespace" },
            },
            Context()
        );

        response.Batch.State.Should().Be(BatchState.Queued);
        response.Upload.Should().BeNull();
        response.Batch.Export.OriginalValueColumn.Should().Be("original");
        response.Batch.Export.PseudonymValueColumn.Should().Be("pseudonym");
        A.CallTo(() => backgroundJobClient.Create(A<Job>._, A<IState>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Create_WithWindows1252_ShouldStoreItAndReportItBack()
    {
        var sut = CreateSut();
        var request = PseudonymizeRequest();
        request.Transform.Format = new CsvFormat { Encoding = CsvEncoding.Windows1252 };

        var response = await sut.Create(request, Context());

        response.Batch.Transform.Format.Encoding.Should().Be(CsvEncoding.Windows1252);
        var stored = await repository.FindAsync(
            Guid.Parse(response.Batch.Id),
            CancellationToken.None
        );
        stored!.Encoding.Should().Be("windows-1252");
    }

    [Fact]
    public async Task Create_WithAnImport_ShouldDefaultItsColumnNames()
    {
        var sut = CreateSut();

        var response = await sut.Create(
            new BatchServiceCreateRequest
            {
                Import = new BatchImportSpec { Namespace = "existingNamespace" },
            },
            Context()
        );

        var stored = await repository.FindAsync(
            Guid.Parse(response.Batch.Id),
            CancellationToken.None
        );
        stored!.Direction.Should().Be(PseudonymizationJobDirection.Import);
        var mapping = stored.ColumnMappings.Should().ContainSingle().Subject;
        mapping.SourceColumn.Should().Be("original");
        mapping.TargetColumn.Should().Be("pseudonym");
        mapping.NamespaceColumn.Should().BeNull();
    }

    [Fact]
    public async Task Create_WithAnImportFromANamespaceColumn_ShouldStoreTheColumn()
    {
        var sut = CreateSut();

        var response = await sut.Create(
            new BatchServiceCreateRequest
            {
                Import = new BatchImportSpec { NamespaceColumn = "namespace" },
            },
            Context()
        );

        response.Batch.Import.NamespaceColumn.Should().Be("namespace");
        response.Batch.Import.Namespace.Should().BeEmpty();
    }

    public static TheoryData<BatchServiceCreateRequest> InvalidCreateRequests() =>
        new()
        {
            // no kind at all
            new BatchServiceCreateRequest(),
            // a transform without a direction
            new BatchServiceCreateRequest
            {
                Transform = new BatchTransformSpec
                {
                    ColumnMappings =
                    {
                        new BatchColumnMapping
                        {
                            SourceColumn = "a",
                            Namespace = "existingNamespace",
                        },
                    },
                },
            },
            // a transform without columns
            new BatchServiceCreateRequest
            {
                Transform = new BatchTransformSpec
                {
                    Direction = BatchTransformDirection.Pseudonymize,
                },
            },
            // a column mapping without a namespace
            new BatchServiceCreateRequest
            {
                Transform = new BatchTransformSpec
                {
                    Direction = BatchTransformDirection.Pseudonymize,
                    ColumnMappings = { new BatchColumnMapping { SourceColumn = "a" } },
                },
            },
            // a pseudonymize mode on a de-pseudonymizing transform
            new BatchServiceCreateRequest
            {
                Transform = new BatchTransformSpec
                {
                    Direction = BatchTransformDirection.Depseudonymize,
                    PseudonymizeMode = BatchPseudonymizeMode.FailIfMissing,
                    ColumnMappings =
                    {
                        new BatchColumnMapping
                        {
                            SourceColumn = "a",
                            Namespace = "existingNamespace",
                        },
                    },
                },
            },
            // a delimiter CSV parsing can't use
            new BatchServiceCreateRequest
            {
                Export = new BatchExportSpec
                {
                    Namespace = "existingNamespace",
                    Format = new CsvFormat { Delimiter = "||" },
                },
            },
            // an encoding that doesn't exist
            new BatchServiceCreateRequest
            {
                Export = new BatchExportSpec
                {
                    Namespace = "existingNamespace",
                    Format = new CsvFormat { Encoding = (CsvEncoding)99 },
                },
            },
            // an import naming both a namespace and a namespace column
            new BatchServiceCreateRequest
            {
                Import = new BatchImportSpec
                {
                    Namespace = "existingNamespace",
                    NamespaceColumn = "namespace",
                },
            },
            // an import naming neither
            new BatchServiceCreateRequest { Import = new BatchImportSpec() },
            // an export without a namespace
            new BatchServiceCreateRequest { Export = new BatchExportSpec() },
        };

    [Theory]
    [MemberData(nameof(InvalidCreateRequests))]
    public async Task Create_WithAnInvalidRequest_ShouldThrowInvalidArgumentWithoutCreatingABatch(
        BatchServiceCreateRequest request
    )
    {
        var sut = CreateSut();

        var act = () => sut.Create(request, Context());

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.InvalidArgument);
        (await repository.ListAsync(null, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithoutAccessToTheNamespace_ShouldThrowPermissionDenied()
    {
        var sut = CreateSut(new AuthorizationConfig { IsEnabled = true });

        var act = () => sut.Create(PseudonymizeRequest(), Context());

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Get_BeforeTheUploadArrived_ShouldLeaveTheBatchAwaitingIt()
    {
        var sut = CreateSut();
        var created = await sut.Create(PseudonymizeRequest(), Context());
        InputNotUploadedYet();

        var response = await sut.Get(
            new BatchServiceGetRequest { BatchId = created.Batch.Id },
            Context()
        );

        response.Batch.State.Should().Be(BatchState.AwaitingUpload);
        response.Download.Should().BeNull();
        A.CallTo(() => backgroundJobClient.Create(A<Job>._, A<IState>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Get_AfterTheUploadArrived_ShouldQueueTheBatch()
    {
        // The whole point of polling: no separate "upload finished" call is needed.
        var sut = CreateSut();
        var created = await sut.Create(PseudonymizeRequest(), Context());

        var response = await sut.Get(
            new BatchServiceGetRequest { BatchId = created.Batch.Id },
            Context()
        );

        response.Batch.State.Should().Be(BatchState.Queued);
        response.Batch.Progress.TotalBytes.Should().Be(1234);
        A.CallTo(() => backgroundJobClient.Create(A<Job>._, A<IState>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Get_Repeatedly_ShouldQueueTheBatchOnlyOnce()
    {
        var sut = CreateSut();
        var created = await sut.Create(PseudonymizeRequest(), Context());
        var request = new BatchServiceGetRequest { BatchId = created.Batch.Id };

        await sut.Get(request, Context());
        await sut.Get(request, Context());
        await sut.Get(request, Context());

        A.CallTo(() => backgroundJobClient.Create(A<Job>._, A<IState>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Get_OnACompletedBatch_ShouldReturnTheDownloadTarget()
    {
        var sut = CreateSut();
        var created = await sut.Create(PseudonymizeRequest(), Context());
        var id = Guid.Parse(created.Batch.Id);
        await sut.Get(new BatchServiceGetRequest { BatchId = created.Batch.Id }, Context());
        await repository.MarkRunningAsync(id, CancellationToken.None);
        await repository.CompleteAsync(id, "csv-jobs/output.csv", 10, CancellationToken.None);

        var response = await sut.Get(
            new BatchServiceGetRequest { BatchId = created.Batch.Id },
            Context()
        );

        response.Batch.State.Should().Be(BatchState.Completed);
        response.Batch.Progress.RowsProcessed.Should().Be(10);
        response.Download.Method.Should().Be("GET");
        response.Download.Uri.Should().Be("https://example.invalid/presigned");
        response.Download.Headers.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_WithAnUnknownBatch_ShouldThrowNotFound()
    {
        var sut = CreateSut();

        var act = () =>
            sut.Get(new BatchServiceGetRequest { BatchId = Guid.NewGuid().ToString() }, Context());

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task Get_WithAMalformedId_ShouldThrowInvalidArgument()
    {
        var sut = CreateSut();

        var act = () => sut.Get(new BatchServiceGetRequest { BatchId = "nope" }, Context());

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task List_ShouldPageThroughEveryBatchNewestFirst()
    {
        var sut = CreateSut();
        var createdIds = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            createdIds.Add((await sut.Create(PseudonymizeRequest(), Context())).Batch.Id);
        }

        var seen = new List<string>();
        var pageToken = string.Empty;
        var pages = 0;
        do
        {
            var page = await sut.List(
                new BatchServiceListRequest { PageSize = 2, PageToken = pageToken },
                Context()
            );
            page.Batches.Count.Should().BeLessThanOrEqualTo(2);
            seen.AddRange(page.Batches.Select(b => b.Id));
            pageToken = page.NextPageToken;
            pages++;
        } while (!string.IsNullOrEmpty(pageToken) && pages < 10);

        seen.Should().Equal(Enumerable.Reverse(createdIds));
    }

    [Fact]
    public async Task List_WithAPageTokenItDidNotIssue_ShouldThrowInvalidArgument()
    {
        var sut = CreateSut();

        var act = () =>
            sut.List(new BatchServiceListRequest { PageToken = "not-a-token" }, Context());

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Cancel_ShouldReturnTheCancelledBatch()
    {
        var sut = CreateSut();
        var created = await sut.Create(PseudonymizeRequest(), Context());

        var response = await sut.Cancel(
            new BatchServiceCancelRequest { BatchId = created.Batch.Id },
            Context()
        );

        response.Batch.State.Should().Be(BatchState.Cancelled);
    }

    [Fact]
    public async Task Delete_OnAFinishedBatch_ShouldRemoveIt()
    {
        var sut = CreateSut();
        var created = await sut.Create(PseudonymizeRequest(), Context());
        await sut.Cancel(new BatchServiceCancelRequest { BatchId = created.Batch.Id }, Context());

        await sut.Delete(new BatchServiceDeleteRequest { BatchId = created.Batch.Id }, Context());

        var act = () =>
            sut.Get(new BatchServiceGetRequest { BatchId = created.Batch.Id }, Context());
        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_OnABatchStillAwaitingItsUpload_ShouldThrowFailedPrecondition()
    {
        var sut = CreateSut();
        var created = await sut.Create(PseudonymizeRequest(), Context());

        var act = () =>
            sut.Delete(new BatchServiceDeleteRequest { BatchId = created.Batch.Id }, Context());

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.FailedPrecondition);
        (await repository.FindAsync(Guid.Parse(created.Batch.Id), CancellationToken.None))
            .Should()
            .NotBeNull();
    }
}
