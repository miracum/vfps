using System.Buffers;

namespace Vfps.Voprf.Internal;

/// <summary>
/// Builds the length-framed byte strings RFC 9497 hashes.
/// </summary>
/// <remarks>
/// Almost every transcript in the specification is a sequence of
/// <c>I2OSP(len(x), 2) || x</c> chunks followed by a literal label. The framing is what
/// keeps a transcript unambiguous: without it, two different splits of the same
/// concatenated bytes would hash alike, and a server could answer one question with the
/// proof for another.
/// </remarks>
internal sealed class Transcript
{
    private readonly ArrayBufferWriter<byte> writer;

    /// <summary>Builds a transcript.</summary>
    /// <param name="capacity">
    /// Expected size in bytes. Worth getting exactly right for a transcript that will be
    /// cleared: <see cref="ArrayBufferWriter{T}"/> grows by allocating a larger array and
    /// copying, and the array it abandons is not something <see cref="Clear"/> can reach.
    /// </param>
    public Transcript(int capacity = 256) => writer = new ArrayBufferWriter<byte>(capacity);

    /// <summary>Appends <c>I2OSP(len(value), 2) || value</c>.</summary>
    /// <exception cref="VoprfException">The value is longer than a 16-bit length can frame.</exception>
    public Transcript AddFramed(ReadOnlySpan<byte> value)
    {
        if (value.Length > ushort.MaxValue)
        {
            throw new VoprfException(
                $"Cannot frame {value.Length} bytes; RFC 9497 transcripts carry lengths as uint16."
            );
        }

        AddUInt16((ushort)value.Length);
        writer.Write(value);
        return this;
    }

    /// <summary>Appends <c>I2OSP(value, 2)</c>: a bare big-endian counter, not a length.</summary>
    public Transcript AddUInt16(ushort value)
    {
        var span = writer.GetSpan(2);
        span[0] = (byte)(value >> 8);
        span[1] = (byte)value;
        writer.Advance(2);
        return this;
    }

    /// <summary>Appends bytes with no framing, for the trailing label.</summary>
    public Transcript AddRaw(ReadOnlySpan<byte> value)
    {
        writer.Write(value);
        return this;
    }

    public ReadOnlySpan<byte> Span => writer.WrittenSpan;

    /// <summary>
    /// Zeroes what has been written. Needed only for a transcript carrying secrets - most of
    /// them here frame group elements and hashes that are public by construction.
    /// </summary>
    public void Clear() => writer.Clear();
}
