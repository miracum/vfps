using System.Security.Cryptography;

namespace Vfps.Voprf.Internal;

/// <summary>
/// RFC 9380 <c>expand_message_xmd</c> over SHA-512.
/// </summary>
/// <remarks>
/// Both <c>HashToGroup</c> and <c>HashToScalar</c> reach the group through this expander:
/// it turns a message and a domain separation tag into uniformly distributed bytes, which
/// are then mapped onto the group or reduced into a scalar. Keeping it in managed code
/// means libsodium is only ever asked for curve arithmetic.
/// </remarks>
internal static class ExpandMessage
{
    /// <summary>SHA-512's output size, <c>b_in_bytes</c> in RFC 9380.</summary>
    private const int HashBytes = 64;

    /// <summary>SHA-512's input block size, <c>s_in_bytes</c> in RFC 9380.</summary>
    private const int BlockBytes = 128;

    /// <summary>Largest DST the construction can frame; its length is carried as one byte.</summary>
    private const int MaxDstLength = 255;

    /// <summary>
    /// Expands <paramref name="message"/> into <paramref name="lengthInBytes"/> uniform
    /// bytes under <paramref name="domainSeparationTag"/>.
    /// </summary>
    public static byte[] Xmd(
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> domainSeparationTag,
        int lengthInBytes
    )
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            domainSeparationTag.Length,
            MaxDstLength,
            nameof(domainSeparationTag)
        );
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthInBytes, nameof(lengthInBytes));

        // ell = ceil(len_in_bytes / b_in_bytes), and I2OSP(ell, 1) must not overflow.
        var blocks = (lengthInBytes + HashBytes - 1) / HashBytes;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            blocks,
            byte.MaxValue,
            nameof(lengthInBytes)
        );

        // DST_prime = DST || I2OSP(len(DST), 1)
        Span<byte> dstPrime = stackalloc byte[domainSeparationTag.Length + 1];
        domainSeparationTag.CopyTo(dstPrime);
        dstPrime[^1] = (byte)domainSeparationTag.Length;

        // msg_prime = Z_pad || msg || I2OSP(len_in_bytes, 2) || I2OSP(0, 1) || DST_prime
        //
        // Z_pad is a full input block of zeroes. It makes the first hash absorb a whole
        // block before the message, so that expand_message_xmd stays indifferentiable even
        // when the caller controls the message.
        var msgPrime = new byte[BlockBytes + message.Length + 2 + 1 + dstPrime.Length];
        var at = BlockBytes;
        message.CopyTo(msgPrime.AsSpan(at));
        at += message.Length;
        msgPrime[at++] = (byte)(lengthInBytes >> 8);
        msgPrime[at++] = (byte)lengthInBytes;
        msgPrime[at++] = 0x00;
        dstPrime.CopyTo(msgPrime.AsSpan(at));

        // b_1 = H(b_0 || I2OSP(1, 1) || DST_prime)
        // b_i = H(strxor(b_0, b_(i-1)) || I2OSP(i, 1) || DST_prime)
        var blockInput = new byte[HashBytes + 1 + dstPrime.Length];
        dstPrime.CopyTo(blockInput.AsSpan(HashBytes + 1));

        var b0 = SHA512.HashData(msgPrime);
        var result = new byte[lengthInBytes];
        var previous = b0;
        var written = 0;

        try
        {
            for (var i = 1; i <= blocks; i++)
            {
                if (i == 1)
                {
                    previous.CopyTo(blockInput.AsSpan(0, HashBytes));
                }
                else
                {
                    for (var j = 0; j < HashBytes; j++)
                    {
                        blockInput[j] = (byte)(b0[j] ^ previous[j]);
                    }
                }

                blockInput[HashBytes] = (byte)i;

                var block = SHA512.HashData(blockInput);
                var take = Math.Min(HashBytes, lengthInBytes - written);
                block.AsSpan(0, take).CopyTo(result.AsSpan(written));
                written += take;

                if (!ReferenceEquals(previous, b0))
                {
                    CryptographicOperations.ZeroMemory(previous);
                }

                previous = block;
            }

            return result;
        }
        finally
        {
            // msgPrime holds the caller's message verbatim, and for HashToGroup that message is
            // the identifier being pseudonymized - the very bytes VoprfRequest pins and zeroes.
            // Leaving a plain copy of it here would make that care pointless. b0 and the block
            // chain are deterministic functions of the same message, so they go too.
            //
            // This narrows the window rather than closing it: SHA512.HashData buffers internally
            // where nothing here can reach, and these arrays are not pinned - they are short
            // lived and sized by the caller's input, so pinning every one of them would fragment
            // the heap for a guarantee the runtime does not offer anyway.
            CryptographicOperations.ZeroMemory(msgPrime);
            CryptographicOperations.ZeroMemory(b0);
            CryptographicOperations.ZeroMemory(blockInput);

            if (!ReferenceEquals(previous, b0))
            {
                CryptographicOperations.ZeroMemory(previous);
            }
        }
    }
}
