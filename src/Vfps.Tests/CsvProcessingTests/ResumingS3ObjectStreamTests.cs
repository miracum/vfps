using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using Vfps.CsvProcessing;

namespace Vfps.Tests.CsvProcessingTests;

public class ResumingS3ObjectStreamTests
{
    private const string Bucket = "test-bucket";
    private const string Key = "csv-jobs/input.csv";

    private readonly IAmazonS3 s3 = A.Fake<IAmazonS3>();

    /// <summary>
    /// A stream over <paramref name="content"/> that throws the exact failure a dropped S3
    /// connection produces, once it has handed out <paramref name="failAfterBytes"/> bytes.
    /// </summary>
    private sealed class FailingStream(byte[] content, int failAfterBytes, Exception failure)
        : MemoryStream(content)
    {
        private bool hasFailed;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (!hasFailed && Position >= failAfterBytes)
            {
                hasFailed = true;
                throw failure;
            }

            // Never hand out more than up to the failure point in one read, so the failure lands
            // mid-content rather than after everything was already delivered.
            var allowed = hasFailed
                ? buffer.Length
                : Math.Min(buffer.Length, failAfterBytes - (int)Position);

            return base.ReadAsync(buffer[..allowed], cancellationToken);
        }
    }

    private static Exception PrematureEnd() =>
        new HttpIOException(
            HttpRequestError.ResponseEnded,
            "The response ended prematurely, with at least 82245297 additional bytes expected."
        );

    /// <summary>
    /// Serves the object, failing the first response part-way through and answering the resumed
    /// range request with the remainder. Records every request for assertions.
    /// </summary>
    private List<GetObjectRequest> FakeObject(
        string content,
        int failAfterBytes,
        Exception? failure = null,
        string etag = "\"etag-1\""
    )
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var rangeRequests = new List<GetObjectRequest>();

        A.CallTo(() => s3.GetObjectAsync(Bucket, Key, A<CancellationToken>._))
            .Returns(
                new GetObjectResponse
                {
                    ETag = etag,
                    ContentLength = bytes.Length,
                    ResponseStream = new FailingStream(
                        bytes,
                        failAfterBytes,
                        failure ?? PrematureEnd()
                    ),
                }
            );

        A.CallTo(() => s3.GetObjectAsync(A<GetObjectRequest>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                var request = call.GetArgument<GetObjectRequest>(0)!;
                rangeRequests.Add(request);
                var start = (int)(request.ByteRange?.Start ?? 0);

                return Task.FromResult(
                    new GetObjectResponse
                    {
                        ETag = etag,
                        ContentLength = bytes.Length - start,
                        ResponseStream = new MemoryStream(bytes[start..]),
                    }
                );
            });

        return rangeRequests;
    }

    private async Task<string> ReadAllAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return await reader.ReadToEndAsync();
    }

    private async Task<ResumingS3ObjectStream> OpenAsync() =>
        await ResumingS3ObjectStream.OpenAsync(
            s3,
            Bucket,
            Key,
            NullLogger.Instance,
            CancellationToken.None,
            // No point spending the real backoff here - what is under test is that it resumes and
            // where from, not how long it waits first.
            resumeDelay: TimeSpan.Zero
        );

    [Fact]
    public async Task ReadAsync_WhenTheConnectionDropsMidStream_ShouldResumeAndDeliverEveryByte()
    {
        // The failure this exists for: "The response ended prematurely, with at least N additional
        // bytes expected", which the AWS SDK cannot retry itself once bytes have been handed out.
        var content = string.Concat(Enumerable.Range(0, 2000).Select(i => $"row-{i:D6}\n"));
        FakeObject(content, failAfterBytes: 4096);

        using var sut = await OpenAsync();
        var read = await ReadAllAsync(sut);

        read.Should().Be(content);
        sut.BytesRead.Should().Be(Encoding.UTF8.GetByteCount(content));
    }

    [Fact]
    public async Task ReadAsync_WhenResuming_ShouldRequestExactlyTheBytesNotYetDelivered()
    {
        // Off by one here would either lose a byte or duplicate one, and a CSV would parse either
        // way - silently producing wrong output rather than failing.
        var content = string.Concat(Enumerable.Range(0, 2000).Select(i => $"row-{i:D6}\n"));
        var requests = FakeObject(content, failAfterBytes: 4096);

        using var sut = await OpenAsync();
        await ReadAllAsync(sut);

        requests.Should().ContainSingle();
        requests[0].ByteRange!.Start.Should().Be(4096);
        requests[0].ByteRange!.End.Should().Be(Encoding.UTF8.GetByteCount(content) - 1);
    }

    [Fact]
    public async Task ReadAsync_WhenResuming_ShouldPinTheObjectItStartedReading()
    {
        // Without If-Match, an object replaced mid-job would have its tail spliced onto the head
        // of the old one, producing output matching neither.
        var content = string.Concat(Enumerable.Range(0, 2000).Select(i => $"row-{i:D6}\n"));
        var requests = FakeObject(content, failAfterBytes: 4096, etag: "\"the-original\"");

        using var sut = await OpenAsync();
        await ReadAllAsync(sut);

        requests[0].EtagToMatch.Should().Be("\"the-original\"");
    }

    [Fact]
    public async Task ReadAsync_WhenTheObjectChangedUnderIt_ShouldFailWithAClearReason()
    {
        var content = string.Concat(Enumerable.Range(0, 2000).Select(i => $"row-{i:D6}\n"));
        FakeObject(content, failAfterBytes: 4096);
        A.CallTo(() => s3.GetObjectAsync(A<GetObjectRequest>._, A<CancellationToken>._))
            .Throws(
                new AmazonS3Exception("precondition failed")
                {
                    StatusCode = HttpStatusCode.PreconditionFailed,
                }
            );

        using var sut = await OpenAsync();
        var act = async () => await ReadAllAsync(sut);

        // Not the raw 412: "response ended prematurely" would not have hinted at the real cause.
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*changed*");
    }

    [Fact]
    public async Task ReadAsync_WhenTheFailureKeepsRepeating_ShouldGiveUpRatherThanLoopForever()
    {
        var content = string.Concat(Enumerable.Range(0, 2000).Select(i => $"row-{i:D6}\n"));
        FakeObject(content, failAfterBytes: 4096);
        // Every resumed response fails the same way, immediately - a store that is down rather
        // than a connection that blipped.
        A.CallTo(() => s3.GetObjectAsync(A<GetObjectRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
                Task.FromResult(
                    new GetObjectResponse
                    {
                        ETag = "\"etag-1\"",
                        ContentLength = 1,
                        ResponseStream = new FailingStream([1], 0, PrematureEnd()),
                    }
                )
            );

        using var sut = await OpenAsync();
        var act = async () => await ReadAllAsync(sut);

        await act.Should().ThrowAsync<HttpIOException>();
    }

    [Fact]
    public async Task ReadAsync_WithAnErrorThatWouldFailIdentically_ShouldNotRetryIt()
    {
        // A 403 does not become a 200 by asking again; only a dropped connection is worth
        // resuming.
        var content = "some,content\n";
        FakeObject(
            content,
            failAfterBytes: 4,
            failure: new AmazonS3Exception("access denied")
            {
                StatusCode = HttpStatusCode.Forbidden,
            }
        );

        using var sut = await OpenAsync();
        var act = async () => await ReadAllAsync(sut);

        await act.Should().ThrowAsync<AmazonS3Exception>();
        A.CallTo(() => s3.GetObjectAsync(A<GetObjectRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ReadAsync_WithoutAnyFailure_ShouldNeverReRequestTheObject()
    {
        var content = string.Concat(Enumerable.Range(0, 500).Select(i => $"row-{i:D6}\n"));
        var requests = FakeObject(content, failAfterBytes: int.MaxValue);

        using var sut = await OpenAsync();
        var read = await ReadAllAsync(sut);

        read.Should().Be(content);
        requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TimeFetching_ShouldAccumulateOnlyWhileWaitingOnObjectStorage()
    {
        // The measurement that separates "the store is slow" from "the job has no CPU to parse
        // with" - two causes that look identical in a single read-input phase and have opposite fixes.
        var content = string.Concat(Enumerable.Range(0, 200).Select(i => $"row-{i:D6}\n"));
        FakeObject(content, failAfterBytes: int.MaxValue);

        using var sut = await OpenAsync();
        sut.TimeFetching.Should().Be(TimeSpan.Zero);

        await ReadAllAsync(sut);

        // A MemoryStream returns instantly, so this only proves it is wired to the reads at all -
        // that it moves when bytes are read and not before.
        sut.TimeFetching.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task TimeFetching_ShouldIncludeTimeSpentReconnecting()
    {
        // A resume is time the job spent unable to make progress on input, so it belongs in the
        // same bucket - otherwise a job losing its connection repeatedly would look CPU-bound.
        var content = string.Concat(Enumerable.Range(0, 2000).Select(i => $"row-{i:D6}\n"));
        FakeObject(content, failAfterBytes: 4096);

        using var sut = await ResumingS3ObjectStream.OpenAsync(
            s3,
            Bucket,
            Key,
            NullLogger.Instance,
            CancellationToken.None,
            resumeDelay: TimeSpan.FromMilliseconds(200)
        );
        await ReadAllAsync(sut);

        // Not asserted at the full 200ms: Task.Delay can return a fraction of a millisecond early
        // on a coarse timer, and what matters here is that the reconnect wait is counted at all
        // rather than that it is counted to the microsecond.
        sut.TimeFetching.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150));
    }
}
