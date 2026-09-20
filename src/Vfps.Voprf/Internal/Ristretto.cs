using Vfps.Voprf.Native;

namespace Vfps.Voprf.Internal;

/// <summary>
/// The prime-order group operations RFC 9497 is written against, over ristretto255.
/// </summary>
/// <remarks>
/// Each method here is one libsodium call plus the buffer handling around it. The names
/// follow RFC 9497 section 2.1 rather than libsodium's, so the protocol code below reads
/// like the specification it implements.
/// </remarks>
internal static unsafe class Ristretto
{
    /// <summary>Length of a serialised scalar (Ns).</summary>
    public const int ScalarBytes = 32;

    /// <summary>Length of a serialised element (Ne).</summary>
    public const int ElementBytes = 32;

    /// <summary>Length of the uniform input to <see cref="HashToGroupMap"/> and <see cref="ScalarFromWide"/>.</summary>
    public const int WideBytes = 64;

    /// <summary>A uniformly random non-zero scalar. RFC 9497 <c>RandomScalar</c>.</summary>
    public static byte[] RandomScalar()
    {
        // Pinned: the blinding scalar and the proof nonce are both secrets whose leftover
        // copies we would rather the GC not scatter.
        var scalar = GC.AllocateArray<byte>(ScalarBytes, pinned: true);
        fixed (byte* r = scalar)
        {
            Sodium.crypto_core_ristretto255_scalar_random(r);
        }

        return scalar;
    }

    /// <summary>
    /// Reduces 64 uniform bytes, read as a little-endian integer, modulo the group order.
    /// This is the second half of RFC 9497's <c>HashToScalar</c> for this ciphersuite.
    /// </summary>
    /// <param name="wide">The 64 uniform bytes to reduce.</param>
    /// <param name="pinned">
    /// Whether the result is pinned. Pass <see langword="true"/> when the scalar is secret - a
    /// relocated buffer leaves its old contents behind where nothing can zero them, so pinning
    /// is what makes a later <c>ZeroMemory</c> mean anything.
    /// </param>
    public static byte[] ScalarFromWide(ReadOnlySpan<byte> wide, bool pinned = false)
    {
        CheckLength(wide.Length, WideBytes, nameof(wide));

        var scalar = pinned
            ? GC.AllocateArray<byte>(ScalarBytes, pinned: true)
            : new byte[ScalarBytes];
        fixed (byte* r = scalar)
        fixed (byte* s = wide)
        {
            Sodium.crypto_core_ristretto255_scalar_reduce(r, s);
        }

        return scalar;
    }

    /// <summary>Maps 64 uniform bytes onto the group. RFC 9497 <c>HashToGroup</c>'s final step.</summary>
    public static byte[] HashToGroupMap(ReadOnlySpan<byte> wide)
    {
        CheckLength(wide.Length, WideBytes, nameof(wide));

        var element = new byte[ElementBytes];
        fixed (byte* p = element)
        fixed (byte* r = wide)
        {
            Sodium.crypto_core_ristretto255_from_hash(p, r);
        }

        return element;
    }

    /// <summary>RFC 9497 <c>ScalarInverse</c>.</summary>
    public static byte[] ScalarInverse(ReadOnlySpan<byte> scalar)
    {
        CheckLength(scalar.Length, ScalarBytes, nameof(scalar));

        var inverse = GC.AllocateArray<byte>(ScalarBytes, pinned: true);
        fixed (byte* recip = inverse)
        fixed (byte* s = scalar)
        {
            if (Sodium.crypto_core_ristretto255_scalar_invert(recip, s) != 0)
            {
                throw new VoprfException("Cannot invert the zero scalar.");
            }
        }

        return inverse;
    }

    /// <summary>Multiplies two scalars modulo the group order.</summary>
    /// <param name="x">First operand.</param>
    /// <param name="y">Second operand.</param>
    /// <param name="pinned">
    /// Whether the product is pinned, for a result the caller intends to zero. See
    /// <see cref="ScalarFromWide"/>.
    /// </param>
    public static byte[] MultiplyScalars(
        ReadOnlySpan<byte> x,
        ReadOnlySpan<byte> y,
        bool pinned = false
    )
    {
        CheckLength(x.Length, ScalarBytes, nameof(x));
        CheckLength(y.Length, ScalarBytes, nameof(y));

        var product = pinned
            ? GC.AllocateArray<byte>(ScalarBytes, pinned: true)
            : new byte[ScalarBytes];
        fixed (byte* z = product)
        fixed (byte* a = x)
        fixed (byte* b = y)
        {
            Sodium.crypto_core_ristretto255_scalar_mul(z, a, b);
        }

        return product;
    }

    /// <summary>Subtracts two scalars modulo the group order.</summary>
    public static byte[] SubtractScalars(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        CheckLength(x.Length, ScalarBytes, nameof(x));
        CheckLength(y.Length, ScalarBytes, nameof(y));

        var difference = new byte[ScalarBytes];
        fixed (byte* z = difference)
        fixed (byte* a = x)
        fixed (byte* b = y)
        {
            Sodium.crypto_core_ristretto255_scalar_sub(z, a, b);
        }

        return difference;
    }

    /// <summary>
    /// Multiplies an element by a scalar, throwing on failure. RFC 9497 writes this
    /// <c>k * A</c>.
    /// </summary>
    /// <exception cref="VoprfException">
    /// The element is not a canonical encoding, or the result is the identity.
    /// </exception>
    public static byte[] ScalarMultiply(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> element)
    {
        if (!TryScalarMultiply(scalar, element, out var result))
        {
            throw new VoprfException(
                "Scalar multiplication failed: the element is not a valid ristretto255 encoding, "
                    + "or the result is the group identity."
            );
        }

        return result;
    }

    /// <summary>
    /// Multiplies an element by a scalar, reporting failure instead of throwing. Proof
    /// verification uses this, so that a malformed proof is rejected rather than raised.
    /// </summary>
    public static bool TryScalarMultiply(
        ReadOnlySpan<byte> scalar,
        ReadOnlySpan<byte> element,
        out byte[] result
    )
    {
        CheckLength(scalar.Length, ScalarBytes, nameof(scalar));
        CheckLength(element.Length, ElementBytes, nameof(element));

        var product = new byte[ElementBytes];
        fixed (byte* q = product)
        fixed (byte* n = scalar)
        fixed (byte* p = element)
        {
            if (Sodium.crypto_scalarmult_ristretto255(q, n, p) != 0)
            {
                result = [];
                return false;
            }
        }

        result = product;
        return true;
    }

    /// <summary>
    /// Multiplies the generator by a scalar, reporting failure instead of throwing.
    /// RFC 9497 <c>ScalarMultGen</c>.
    /// </summary>
    public static bool TryScalarMultiplyGenerator(ReadOnlySpan<byte> scalar, out byte[] result)
    {
        CheckLength(scalar.Length, ScalarBytes, nameof(scalar));

        var product = new byte[ElementBytes];
        fixed (byte* q = product)
        fixed (byte* n = scalar)
        {
            if (Sodium.crypto_scalarmult_ristretto255_base(q, n) != 0)
            {
                result = [];
                return false;
            }
        }

        result = product;
        return true;
    }

    /// <summary>Multiplies the generator by a scalar, throwing on a zero scalar.</summary>
    public static byte[] ScalarMultiplyGenerator(ReadOnlySpan<byte> scalar)
    {
        if (!TryScalarMultiplyGenerator(scalar, out var result))
        {
            throw new VoprfException("Cannot multiply the generator by the zero scalar.");
        }

        return result;
    }

    /// <summary>Adds two elements, reporting failure instead of throwing.</summary>
    public static bool TryAdd(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y, out byte[] result)
    {
        CheckLength(x.Length, ElementBytes, nameof(x));
        CheckLength(y.Length, ElementBytes, nameof(y));

        var sum = new byte[ElementBytes];
        fixed (byte* r = sum)
        fixed (byte* p = x)
        fixed (byte* q = y)
        {
            if (Sodium.crypto_core_ristretto255_add(r, p, q) != 0)
            {
                result = [];
                return false;
            }
        }

        result = sum;
        return true;
    }

    /// <summary>Adds two elements, throwing on an invalid encoding.</summary>
    public static byte[] Add(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        if (!TryAdd(x, y, out var result))
        {
            throw new VoprfException("Cannot add: one of the operands is not a valid element.");
        }

        return result;
    }

    /// <summary>
    /// RFC 9497 <c>DeserializeElement</c>: a canonical encoding of a valid element that is
    /// not the identity. libsodium's validity check covers all three conditions.
    /// </summary>
    public static bool IsValidElement(ReadOnlySpan<byte> element)
    {
        if (element.Length != ElementBytes)
        {
            return false;
        }

        fixed (byte* p = element)
        {
            return Sodium.crypto_core_ristretto255_is_valid_point(p) == 1;
        }
    }

    private static void CheckLength(int actual, int expected, string name)
    {
        if (actual != expected)
        {
            throw new ArgumentException($"Expected exactly {expected} bytes, got {actual}.", name);
        }
    }
}
