using Amazon.S3;
using Amazon.S3.Model;
using Vfps.Data.Models;

namespace Vfps.CsvProcessing;

/// <summary>
/// A write-only <see cref="Stream"/> over an S3 multipart upload that can be resumed after the
/// process writing it dies.
///
/// The job runner used to hand a <c>Pipe</c> to <c>TransferUtility</c>, which is simpler but
/// all-or-nothing: the upload only becomes an object when it finishes, so an attempt killed
/// partway through left nothing behind and the next attempt had to regenerate the whole file.
/// Driving the multipart upload directly means each uploaded part survives independently, and the
/// upload can be picked up later by id - which is what makes
/// <see cref="JobOutputCheckpoint"/> worth anything.
///
/// Parts are never flushed implicitly. Writes accumulate in memory and the caller decides when a
/// part may be cut, via <see cref="TryFlushPartAsync"/> - because a part boundary must also be a
/// CSV row boundary for the checkpoint's row offset to mean anything, and only the caller knows
/// where those are.
/// </summary>
public sealed class MultipartOutputStream : Stream
{
    // S3 requires every part except the last to be at least 5 MiB, so this is the floor on how
    // finely a job can checkpoint. 8 MiB by default (see CsvProcessingConfig.OutputPartSizeBytes)
    // trades a little more re-done work after a crash for fewer round trips.
    public const int MinimumPartSizeBytes = 5 * 1024 * 1024;

    private readonly IAmazonS3 s3;
    private readonly string bucket;
    private readonly string key;
    private readonly int partSizeBytes;
    private readonly List<CompletedOutputPart> parts;
    private readonly MemoryStream buffer = new();

    private MultipartOutputStream(
        IAmazonS3 s3,
        string bucket,
        string key,
        string uploadId,
        int partSizeBytes,
        List<CompletedOutputPart> parts
    )
    {
        this.s3 = s3;
        this.bucket = bucket;
        this.key = key;
        this.partSizeBytes = Math.Max(MinimumPartSizeBytes, partSizeBytes);
        this.parts = parts;
        UploadId = uploadId;
    }

    public string UploadId { get; }

    /// <summary>The object key this upload will become, once completed.</summary>
    public string Key => key;

    /// <summary>Parts durably uploaded so far - the other half of a resumable checkpoint.</summary>
    public IReadOnlyList<CompletedOutputPart> Parts => parts;

    /// <summary>Begins a new multipart upload.</summary>
    public static async Task<MultipartOutputStream> StartAsync(
        IAmazonS3 s3,
        string bucket,
        string key,
        int partSizeBytes,
        CancellationToken cancellationToken
    )
    {
        var initiated = await s3.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest { BucketName = bucket, Key = key },
            cancellationToken
        );

        return new MultipartOutputStream(
            s3,
            bucket,
            key,
            initiated.UploadId,
            partSizeBytes,
            parts: []
        );
    }

    /// <summary>
    /// Re-attaches to the upload a <paramref name="checkpoint"/> describes, or returns
    /// <c>null</c> if the object store no longer has it - the upload may have been aborted by the
    /// bucket's lifecycle rule, or the bucket recreated. A caller that gets <c>null</c> has to
    /// start the job over; there is no partial output to salvage.
    /// </summary>
    public static async Task<MultipartOutputStream?> TryResumeAsync(
        IAmazonS3 s3,
        string bucket,
        JobOutputCheckpoint checkpoint,
        int partSizeBytes,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Deliberately asks the store rather than trusting the checkpoint: the recorded parts
            // are only usable if the upload itself still exists, and finding that out here means
            // one clean fallback instead of a failure at CompleteMultipartUpload, hours later,
            // with the whole job's work already redone.
            await s3.ListPartsAsync(
                new ListPartsRequest
                {
                    BucketName = bucket,
                    Key = checkpoint.ObjectKey,
                    UploadId = checkpoint.UploadId,
                },
                cancellationToken
            );
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchUpload")
        {
            return null;
        }

        return new MultipartOutputStream(
            s3,
            bucket,
            checkpoint.ObjectKey,
            checkpoint.UploadId,
            partSizeBytes,
            [.. checkpoint.Parts]
        );
    }

    /// <summary>
    /// Uploads the buffered bytes as a part if there are enough of them to make a legal one.
    /// Call this only where the buffer ends on a row boundary.
    /// </summary>
    /// <returns><c>true</c> if a part was uploaded, so the caller should record a checkpoint.</returns>
    public async Task<bool> TryFlushPartAsync(CancellationToken cancellationToken)
    {
        if (buffer.Length < partSizeBytes)
        {
            return false;
        }

        await UploadBufferedPartAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Uploads whatever is left as the final part and finalises the object.
    /// </summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        // The last part is exempt from the 5 MiB minimum, so this uploads whatever remains. The
        // buffer can legitimately be empty when the output happened to land exactly on a part
        // boundary - but an upload with no parts at all is rejected, so a job whose output is
        // empty still needs one (empty) part to produce an object.
        if (buffer.Length > 0 || parts.Count == 0)
        {
            await UploadBufferedPartAsync(cancellationToken);
        }

        await s3.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = UploadId,
                PartETags = [.. parts.Select(part => new PartETag(part.PartNumber, part.ETag))],
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Discards the upload and every part already stored for it. For a job that has failed
    /// terminally - an abandoned upload otherwise keeps consuming storage that nothing lists.
    /// </summary>
    public async Task AbortAsync(CancellationToken cancellationToken)
    {
        await s3.AbortMultipartUploadAsync(
            new AbortMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = UploadId,
            },
            cancellationToken
        );
    }

    private async Task UploadBufferedPartAsync(CancellationToken cancellationToken)
    {
        buffer.Position = 0;
        var partNumber = parts.Count + 1;

        var response = await s3.UploadPartAsync(
            new UploadPartRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = UploadId,
                PartNumber = partNumber,
                PartSize = buffer.Length,
                InputStream = buffer,
            },
            cancellationToken
        );

        parts.Add(new CompletedOutputPart { PartNumber = partNumber, ETag = response.ETag });

        buffer.SetLength(0);
        buffer.Position = 0;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    // A no-op on purpose: StreamWriter flushes its own character buffer down to here at row
    // boundaries, which is exactly what has to happen for TryFlushPartAsync to see whole rows -
    // but it must not itself trigger an upload, or parts would be cut wherever the writer
    // happened to flush.
    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Write(byte[] buffer, int offset, int count) =>
        this.buffer.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => this.buffer.Write(buffer);

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    ) => this.buffer.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => this.buffer.WriteAsync(buffer, offset, count, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // Deliberately neither completes nor aborts the upload: which of those is right depends on
        // why the writer is going away, and getting it wrong here would either finalise a
        // half-written object or destroy the very parts a resume depends on. The runner decides.
        if (disposing)
        {
            buffer.Dispose();
        }

        base.Dispose(disposing);
    }
}
