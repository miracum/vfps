using Amazon.S3;
using Amazon.S3.Model;
using FakeItEasy;
using Vfps.CsvProcessing;
using Vfps.Data.Models;

namespace Vfps.Tests.CsvProcessingTests;

public class MultipartOutputStreamTests
{
    private const string Bucket = "test-bucket";
    private const string Key = "csv-jobs/test/output.csv";
    private const string UploadId = "test-upload-id";

    private readonly IAmazonS3 s3 = A.Fake<IAmazonS3>();
    private readonly List<byte[]> uploadedParts = [];

    public MultipartOutputStreamTests()
    {
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
                uploadedParts.Add(copy.ToArray());

                return Task.FromResult(
                    new UploadPartResponse
                    {
                        PartNumber = request.PartNumber,
                        ETag = $"etag-{request.PartNumber}",
                    }
                );
            });
    }

    private Task<MultipartOutputStream> StartAsync() =>
        MultipartOutputStream.StartAsync(
            s3,
            Bucket,
            Key,
            MultipartOutputStream.MinimumPartSizeBytes,
            TestContext.Current.CancellationToken
        );

    [Fact]
    public async Task TryFlushPartAsync_BelowThePartSize_ShouldNotUploadAnything()
    {
        // Parts must never be cut early: S3 rejects any part but the last that is under 5 MiB, so
        // a premature flush would fail the whole upload at completion time.
        await using var sut = await StartAsync();
        await sut.WriteAsync(new byte[1024], TestContext.Current.CancellationToken);

        var flushed = await sut.TryFlushPartAsync(TestContext.Current.CancellationToken);

        flushed.Should().BeFalse();
        uploadedParts.Should().BeEmpty();
        sut.Parts.Should().BeEmpty();
    }

    [Fact]
    public async Task TryFlushPartAsync_AtThePartSize_ShouldUploadAPartAndRecordIt()
    {
        await using var sut = await StartAsync();
        await sut.WriteAsync(
            new byte[MultipartOutputStream.MinimumPartSizeBytes],
            TestContext.Current.CancellationToken
        );

        var flushed = await sut.TryFlushPartAsync(TestContext.Current.CancellationToken);

        flushed.Should().BeTrue();
        uploadedParts.Should().ContainSingle();
        uploadedParts[0].Should().HaveCount(MultipartOutputStream.MinimumPartSizeBytes);
        sut.Parts.Should()
            .BeEquivalentTo(
                [new CompletedOutputPart { PartNumber = 1, ETag = "etag-1" }],
                because: "the part number and ETag are what a checkpoint needs to resume"
            );
    }

    [Fact]
    public async Task TryFlushPartAsync_CalledAgainAfterAFlush_ShouldStartANewPart()
    {
        // The buffer has to be reset, or the second part would repeat the first one's bytes.
        await using var sut = await StartAsync();
        await sut.WriteAsync(
            new byte[MultipartOutputStream.MinimumPartSizeBytes],
            TestContext.Current.CancellationToken
        );
        await sut.TryFlushPartAsync(TestContext.Current.CancellationToken);

        await sut.WriteAsync("tail"u8.ToArray(), TestContext.Current.CancellationToken);
        var flushedAgain = await sut.TryFlushPartAsync(TestContext.Current.CancellationToken);

        flushedAgain.Should().BeFalse("4 bytes is nowhere near a full part");
        await sut.CompleteAsync(TestContext.Current.CancellationToken);

        uploadedParts.Should().HaveCount(2);
        uploadedParts[1].Should().BeEquivalentTo("tail"u8.ToArray());
        sut.Parts.Select(part => part.PartNumber).Should().Equal(1, 2);
    }

    [Fact]
    public async Task CompleteAsync_WithNothingWritten_ShouldStillProduceAnObject()
    {
        // A multipart upload with no parts at all is rejected, so an empty output still needs one
        // (empty) part - otherwise a job whose input had no data rows would fail at the very end.
        await using var sut = await StartAsync();

        await sut.CompleteAsync(TestContext.Current.CancellationToken);

        uploadedParts.Should().ContainSingle();
        uploadedParts[0].Should().BeEmpty();
        A.CallTo(() =>
                s3.CompleteMultipartUploadAsync(
                    A<CompleteMultipartUploadRequest>.That.Matches(r =>
                        r.UploadId == UploadId && r.PartETags.Count == 1
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task TryResumeAsync_WhenTheUploadStillExists_ShouldContinueThePartNumbering()
    {
        A.CallTo(() => s3.ListPartsAsync(A<ListPartsRequest>._, A<CancellationToken>._))
            .Returns(new ListPartsResponse());
        var checkpoint = new JobOutputCheckpoint
        {
            UploadId = UploadId,
            ObjectKey = Key,
            Parts =
            [
                new CompletedOutputPart { PartNumber = 1, ETag = "etag-1" },
                new CompletedOutputPart { PartNumber = 2, ETag = "etag-2" },
            ],
            RowsWritten = 100,
        };

        await using var sut = await MultipartOutputStream.TryResumeAsync(
            s3,
            Bucket,
            checkpoint,
            MultipartOutputStream.MinimumPartSizeBytes,
            TestContext.Current.CancellationToken
        );

        sut.Should().NotBeNull();
        await sut!.WriteAsync("more"u8.ToArray(), TestContext.Current.CancellationToken);
        await sut.CompleteAsync(TestContext.Current.CancellationToken);

        // The next part must be 3, not 1 - reusing a part number would overwrite stored output.
        A.CallTo(() =>
                s3.UploadPartAsync(
                    A<UploadPartRequest>.That.Matches(r => r.PartNumber == 3),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                s3.CompleteMultipartUploadAsync(
                    A<CompleteMultipartUploadRequest>.That.Matches(r => r.PartETags.Count == 3),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task TryResumeAsync_WhenTheUploadIsGone_ShouldReturnNull()
    {
        A.CallTo(() => s3.ListPartsAsync(A<ListPartsRequest>._, A<CancellationToken>._))
            .Throws(new AmazonS3Exception("gone") { ErrorCode = "NoSuchUpload" });

        var resumed = await MultipartOutputStream.TryResumeAsync(
            s3,
            Bucket,
            new JobOutputCheckpoint { UploadId = UploadId, ObjectKey = Key },
            MultipartOutputStream.MinimumPartSizeBytes,
            TestContext.Current.CancellationToken
        );

        resumed.Should().BeNull();
    }
}
