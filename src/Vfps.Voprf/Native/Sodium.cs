using System.Runtime.InteropServices;

namespace Vfps.Voprf.Native;

/// <summary>
/// The libsodium entry points this implementation calls, one-to-one.
/// </summary>
/// <remarks>
/// <para>
/// The native library comes from the <c>libsodium</c> NuGet package, which ships
/// <c>runtimes/&lt;rid&gt;/native/</c> binaries for every runtime identifier. The .NET host
/// resolves them from <c>deps.json</c>, so there is deliberately no loader, no probing, and
/// no build script here.
/// </para>
/// <para>
/// Everything below is ristretto255 arithmetic: a prime-order group built on Curve25519,
/// which is what lets RFC 9497 treat encodings as canonical and comparable. Scalars are
/// 32-byte little-endian integers reduced modulo the group order; elements are 32-byte
/// canonical encodings.
/// </para>
/// </remarks>
internal static unsafe partial class Sodium
{
    private const string Library = "libsodium";

    /// <summary>
    /// libsodium needs <c>sodium_init</c> before its CPU feature detection and RNG are set
    /// up. It is idempotent and safe to call from several threads; the type initialiser
    /// runs it once before any other entry point here can be reached.
    /// </summary>
    static Sodium()
    {
        // 0: initialised now. 1: already initialised. Negative: unusable.
        if (sodium_init() < 0)
        {
            throw new InvalidOperationException(
                "sodium_init() failed; libsodium is unusable in this process."
            );
        }
    }

    [LibraryImport(Library)]
    private static partial int sodium_init();

    /// <summary>Maps 64 uniform bytes to a group element (the ristretto255 one-way map).</summary>
    [LibraryImport(Library)]
    internal static partial void crypto_core_ristretto255_from_hash(byte* p, byte* r);

    /// <summary>
    /// Multiplies element <paramref name="p"/> by scalar <paramref name="n"/>.
    /// Returns -1 if <paramref name="p"/> is not a valid encoding, or if the result is the
    /// identity - which includes the case of a zero scalar.
    /// </summary>
    [LibraryImport(Library)]
    internal static partial int crypto_scalarmult_ristretto255(byte* q, byte* n, byte* p);

    /// <summary>
    /// Multiplies the group generator by scalar <paramref name="n"/>. Returns -1 for a zero
    /// scalar.
    /// </summary>
    [LibraryImport(Library)]
    internal static partial int crypto_scalarmult_ristretto255_base(byte* q, byte* n);

    /// <summary>Adds two elements. Returns -1 if either is not a valid encoding.</summary>
    [LibraryImport(Library)]
    internal static partial int crypto_core_ristretto255_add(byte* r, byte* p, byte* q);

    /// <summary>
    /// Reports whether <paramref name="p"/> is a canonical encoding of a valid element that
    /// is not the identity.
    /// </summary>
    [LibraryImport(Library)]
    internal static partial int crypto_core_ristretto255_is_valid_point(byte* p);

    /// <summary>Fills <paramref name="r"/> with a uniformly random non-zero scalar.</summary>
    [LibraryImport(Library)]
    internal static partial void crypto_core_ristretto255_scalar_random(byte* r);

    /// <summary>
    /// Reduces a 64-byte little-endian integer modulo the group order. This is what turns
    /// the 64 bytes of an <c>expand_message_xmd</c> expansion into a scalar.
    /// </summary>
    [LibraryImport(Library)]
    internal static partial void crypto_core_ristretto255_scalar_reduce(byte* r, byte* s);

    /// <summary>Computes the multiplicative inverse of a scalar. Returns -1 for zero.</summary>
    [LibraryImport(Library)]
    internal static partial int crypto_core_ristretto255_scalar_invert(byte* recip, byte* s);

    /// <summary>Multiplies two scalars modulo the group order.</summary>
    [LibraryImport(Library)]
    internal static partial void crypto_core_ristretto255_scalar_mul(byte* z, byte* x, byte* y);

    /// <summary>Subtracts two scalars modulo the group order.</summary>
    [LibraryImport(Library)]
    internal static partial void crypto_core_ristretto255_scalar_sub(byte* z, byte* x, byte* y);
}
