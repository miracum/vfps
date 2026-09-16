using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace Vfps.CsvProcessing;

/// <summary>
/// The input side of a CSV job: one S3 object, read as a stream that survives its connection being
/// dropped part-way through.
///
/// It has to, because of how long that connection is held open. A job opens the object once and
/// then reads it at the pace of its own processing - a chunk of rows, then a database round trip,
/// then the next chunk - so for a large file the socket sits idle between reads for as long as the
/// upserts take, for as long as the job runs. Anything on the path between the pod and the object
/// store that reaps idle connections (the store itself, a load balancer, a service mesh sidecar, a
/// NAT gateway's conntrack table) eventually closes one, and the next read fails with
/// "The response ended prematurely, with at least N additional bytes expected".
///
/// The AWS SDK cannot retry that itself: its own retry policy covers failures that happen before
/// the response body starts, and once bytes have been handed to a caller it has no idea how many
/// were consumed. This does know - that is what <see cref="ByteCountingStream"/> is already
/// counting for the progress bar - so it re-issues the GET as a range request starting exactly
/// where the last one stopped and carries on. Without it, a dropped connection fails the whole
/// job, and since CSV jobs cannot checkpoint, a multi-gigabyte file starts over from nothing.
/// </summary>
internal sealed class ResumingS3ObjectStream : Stream
{
    // Consecutive failures without a single successful read in between. Reset on every successful
    // read rather than counted for the life of the stream: a job running for hours may legitimately
    // need to resume several times, and capping the total would fail the long jobs this exists to
    // protect. What this bounds is a connection that cannot be re-established at all, which is a
    // genuine outage rather than a blip worth riding out.
    private const int MaxConsecutiveResumes = 5;

    /// <summary>
    /// Waited before re-requesting, so a store that is briefly refusing connections is not hammered
    /// by the retries meant to ride it out. Overridable only so tests do not have to spend it -
    /// there is no reason for a deployment to change it.
    /// </summary>
    internal static readonly TimeSpan DefaultResumeDelay = TimeSpan.FromSeconds(2);

    private readonly IAmazonS3 s3;
    private readonly string bucketName;
    private readonly string key;
    private readonly long contentLength;
    private readonly string? etag;
    private readonly ILogger logger;
    private readonly TimeSpan resumeDelay;

    private GetObjectResponse currentResponse;
    private ByteCountingStream current;
    private long bytesFromClosedResponses;
    private int consecutiveResumes;
    private long fetchTicks;

    private ResumingS3ObjectStream(
        IAmazonS3 s3,
        string bucketName,
        string key,
        GetObjectResponse response,
        ILogger logger,
        TimeSpan resumeDelay
    )
    {
        this.s3 = s3;
        this.bucketName = bucketName;
        this.key = key;
        this.logger = logger;
        this.resumeDelay = resumeDelay;
        contentLength = response.ContentLength;
        // Pinned from the first response and sent back as If-Match on every resume, so an object
        // replaced mid-job fails loudly instead of quietly splicing the tail of a different file
        // onto the head of this one. Absent only if the store does not return one.
        etag = response.ETag;
        currentResponse = response;
        current = new ByteCountingStream(response.ResponseStream);
    }

    /// <summary>Opens <paramref name="key"/> for reading.</summary>
    public static async Task<ResumingS3ObjectStream> OpenAsync(
        IAmazonS3 s3,
        string bucketName,
        string key,
        ILogger logger,
        CancellationToken cancellationToken,
        TimeSpan? resumeDelay = null
    )
    {
        var response = await s3.GetObjectAsync(bucketName, key, cancellationToken);

        return new ResumingS3ObjectStream(
            s3,
            bucketName,
            key,
            response,
            logger,
            resumeDelay ?? DefaultResumeDelay
        );
    }

    /// <summary>
    /// Bytes handed to the caller so far, across every response this has read from - what the job's
    /// progress is computed from, and the offset any resume starts at.
    /// </summary>
    public long BytesRead => bytesFromClosedResponses + current.BytesRead;

    /// <summary>
    /// How long the job has spent inside this stream waiting for bytes - object storage delivering
    /// them, plus any time spent re-establishing a dropped connection.
    ///
    /// This is what <see cref="CsvJobPhase.FetchInput"/> is made of, and the reason it can be a
    /// phase of its own: fetching and parsing interleave far too finely for a scope around the
    /// read loop to separate them, but every byte crosses this stream, so measuring here splits
    /// them exactly.
    /// </summary>
    public TimeSpan TimeFetching => Stopwatch.GetElapsedTime(0, fetchTicks);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => contentLength;

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                try
                {
                    var read = await current.ReadAsync(buffer, cancellationToken);
                    consecutiveResumes = 0;

                    return read;
                }
                catch (Exception ex) when (ShouldResume(ex, cancellationToken))
                {
                    await ResumeAsync(ex, cancellationToken);
                }
            }
        }
        finally
        {
            fetchTicks += Stopwatch.GetTimestamp() - startedAt;
        }
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

    // Synchronous reads go through the same resume path - StreamReader only uses the async ones
    // here, but a Stream that silently lost its resilience on the sync path would be a trap.
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Whether <paramref name="ex"/> is the connection dying rather than something that would fail
    /// again identically. Deliberately narrow: a cancelled job (the runner's own cooperative stop,
    /// or a Hangfire shutdown) and a disposed stream must fall straight through, and so must an S3
    /// error with a real status code behind it - a 403 does not become a 200 by asking twice.
    /// </summary>
    private bool ShouldResume(Exception ex, CancellationToken cancellationToken) =>
        consecutiveResumes < MaxConsecutiveResumes
        && !cancellationToken.IsCancellationRequested
        && BytesRead < contentLength
        && ex
            is IOException
                or SocketException
                or AmazonServiceException { InnerException: IOException or SocketException };

    private async Task ResumeAsync(Exception ex, CancellationToken cancellationToken)
    {
        consecutiveResumes++;
        var resumeFrom = BytesRead;

        logger.LogWarning(
            ex,
            "Reading '{Key}' from object storage failed after {BytesRead} of {ContentLength} "
                + "bytes; re-requesting the remainder (attempt {Attempt} of {MaxAttempts}).",
            key,
            resumeFrom,
            contentLength,
            consecutiveResumes,
            MaxConsecutiveResumes
        );

        bytesFromClosedResponses = resumeFrom;
        await current.DisposeAsync();
        currentResponse.Dispose();

        await Task.Delay(resumeDelay, cancellationToken);

        try
        {
            currentResponse = await s3.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = bucketName,
                    Key = key,
                    // Inclusive on both ends, so the last byte is contentLength - 1.
                    ByteRange = new ByteRange(resumeFrom, contentLength - 1),
                    EtagToMatch = etag,
                },
                cancellationToken
            );
        }
        catch (AmazonS3Exception precondition)
            when (precondition.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // If-Match rejected: the object is not the one this job started reading. Splicing the
            // two together would produce output that matches neither, so fail instead - and say
            // why, since "response ended prematurely" would not have hinted at it.
            throw new InvalidOperationException(
                $"The input object '{key}' changed while the job was reading it, so the read "
                    + "cannot be resumed.",
                precondition
            );
        }

        current = new ByteCountingStream(currentResponse.ResponseStream);
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            current.Dispose();
            currentResponse.Dispose();
        }

        base.Dispose(disposing);
    }
}
