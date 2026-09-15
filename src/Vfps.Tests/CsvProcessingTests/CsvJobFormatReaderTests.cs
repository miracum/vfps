using System.Text;
using Vfps.CsvProcessing;

namespace Vfps.Tests.CsvProcessingTests;

public class CsvJobFormatReaderTests
{
    /// <summary>
    /// Records how many times - and how large - the reads reaching the underlying stream are.
    /// A CSV job's input stream is an S3 response, so each of these is a real await against the
    /// network rather than a memory copy, which is the entire reason the buffer size matters.
    /// </summary>
    private sealed class ReadCountingStream(Stream inner) : Stream
    {
        public int ReadCalls { get; private set; }
        public int LargestRequest { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        private void Record(int requested)
        {
            ReadCalls++;
            LargestRequest = Math.Max(LargestRequest, requested);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Record(count);
            return inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            Record(buffer.Length);
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            Record(count);
            return await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task CreateReader_ShouldPullTheInputInLargeChunksRatherThanStreamReadersDefault()
    {
        // 1 MiB of payload. StreamReader's own default buffer is 1 KiB, which would take upwards
        // of a thousand reads to consume this; the explicit buffer should need a small fraction
        // of that. The bound is deliberately loose - this guards against silently falling back to
        // the default constructor, not against a particular buffer size.
        var payload = new string('x', 1024 * 1024);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var counting = new ReadCountingStream(source);

        using var reader = CsvJobFormat.CreateReader(counting, Encoding.UTF8);
        var read = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        read.Should().HaveLength(payload.Length);
        counting.ReadCalls.Should().BeLessThan(100);
        counting.LargestRequest.Should().BeGreaterThanOrEqualTo(16 * 1024);
    }

    [Fact]
    public async Task CreateReader_ShouldStripAUtf8ByteOrderMark()
    {
        // Excel writes one, and a job whose first column is mapped would otherwise stop resolving
        // that column: the BOM would arrive as part of the first header cell's name. Locked in
        // because supplying an explicit buffer size means also supplying this flag by hand.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("id,value\n1,a\n"));
        using var source = new MemoryStream([.. bytes]);

        using var reader = CsvJobFormat.CreateReader(source, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        text.Should().StartWith("id,value");
        text.Should().NotContain("﻿");
    }

    [Fact]
    public async Task CreateReader_ShouldHonourANonUtf8Encoding()
    {
        // job.Encoding is operator-supplied per job, so the reader has to actually use it rather
        // than assume UTF-8 - a latin1 'ä' is one byte there and two in UTF-8.
        var latin1 = Encoding.Latin1;
        using var source = new MemoryStream(latin1.GetBytes("id,value\n1,Müller\n"));

        using var reader = CsvJobFormat.CreateReader(source, latin1);
        var text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        text.Should().Contain("Müller");
    }
}
