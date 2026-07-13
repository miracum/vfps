using Vfps.CsvProcessing;

namespace Vfps.Tests.CsvProcessingTests;

public class ByteCountingStreamTests
{
    private static ByteCountingStream CreateSut(byte[] content) => new(new MemoryStream(content));

    [Fact]
    public void Read_CalledOnce_ShouldTrackBytesRead()
    {
        var sut = CreateSut([1, 2, 3, 4, 5]);
        var buffer = new byte[3];

        var read = sut.Read(buffer, 0, buffer.Length);

        read.Should().Be(3);
        sut.BytesRead.Should().Be(3);
    }

    [Fact]
    public void Read_CalledRepeatedlyUntilExhausted_ShouldAccumulateBytesRead()
    {
        var sut = CreateSut([1, 2, 3, 4, 5]);
        var buffer = new byte[2];

        while (sut.Read(buffer, 0, buffer.Length) > 0) { }

        sut.BytesRead.Should().Be(5);
    }

    [Fact]
    public async Task ReadAsync_WithByteArrayOverload_ShouldTrackBytesRead()
    {
        var sut = CreateSut([1, 2, 3, 4, 5]);
        var buffer = new byte[3];

        var read = await sut.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);

        read.Should().Be(3);
        sut.BytesRead.Should().Be(3);
    }

    [Fact]
    public async Task ReadAsync_WithMemoryOverload_ShouldTrackBytesRead()
    {
        var sut = CreateSut([1, 2, 3, 4, 5]);
        var buffer = new byte[3];

        var read = await sut.ReadAsync(buffer.AsMemory(), CancellationToken.None);

        read.Should().Be(3);
        sut.BytesRead.Should().Be(3);
    }

    [Fact]
    public async Task ReadAsync_MixingBothOverloadsAcrossCalls_ShouldAccumulateAcrossBoth()
    {
        var sut = CreateSut([1, 2, 3, 4, 5, 6]);
        var buffer = new byte[3];

        await sut.ReadExactlyAsync(buffer, 0, buffer.Length, CancellationToken.None);
        await sut.ReadExactlyAsync(buffer.AsMemory(), CancellationToken.None);

        sut.BytesRead.Should().Be(6);
    }

    [Fact]
    public void CanRead_ShouldBeTrue()
    {
        CreateSut([]).CanRead.Should().BeTrue();
    }

    [Fact]
    public void CanSeek_ShouldBeFalse()
    {
        CreateSut([]).CanSeek.Should().BeFalse();
    }

    [Fact]
    public void CanWrite_ShouldBeFalse()
    {
        CreateSut([]).CanWrite.Should().BeFalse();
    }

    [Fact]
    public void Length_ShouldDelegateToInnerStream()
    {
        CreateSut([1, 2, 3]).Length.Should().Be(3);
    }

    [Fact]
    public void Position_Get_ShouldDelegateToInnerStream()
    {
        var sut = CreateSut([1, 2, 3]);
        sut.ReadExactly(new byte[2], 0, 2);

        sut.Position.Should().Be(2);
    }

    [Fact]
    public void Position_Set_ShouldThrowNotSupportedException()
    {
        var sut = CreateSut([1, 2, 3]);
        var act = () => sut.Position = 0;

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Seek_ShouldThrowNotSupportedException()
    {
        var sut = CreateSut([1, 2, 3]);
        var act = () => sut.Seek(0, SeekOrigin.Begin);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void SetLength_ShouldThrowNotSupportedException()
    {
        var sut = CreateSut([1, 2, 3]);
        var act = () => sut.SetLength(10);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Write_ShouldThrowNotSupportedException()
    {
        var sut = CreateSut([1, 2, 3]);
        var act = () => sut.Write([1, 2, 3], 0, 3);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Flush_ShouldNotThrow()
    {
        var sut = CreateSut([1, 2, 3]);
        var act = sut.Flush;

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_ShouldDisposeInnerStream()
    {
        var inner = new MemoryStream([1, 2, 3]);
        var sut = new ByteCountingStream(inner);

        sut.Dispose();

        var act = () => inner.ReadByte();
        act.Should().Throw<ObjectDisposedException>();
    }
}
