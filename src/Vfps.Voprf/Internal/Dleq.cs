using System.Security.Cryptography;

namespace Vfps.Voprf.Internal;

/// <summary>
/// The discrete logarithm equality proof of RFC 9497 section 2.2, over ristretto255.
/// </summary>
/// <remarks>
/// <para>
/// The server proves that the scalar behind its published public key is the same scalar it
/// applied to the client's elements, without revealing it. A batch of elements is collapsed
/// into a single pair by a random linear combination whose coefficients are derived from
/// the elements themselves, so a request carrying fifty inputs still needs one proof - and
/// a server cannot choose the coefficients, because they depend on what it is proving.
/// </para>
/// <para>
/// Throughout, <c>A</c> is the group generator, which is the only value RFC 9497's VOPRF
/// mode ever passes, so it is implicit here rather than a parameter.
/// </para>
/// </remarks>
internal static class Dleq
{
    /// <summary>
    /// RFC 9497 <c>GenerateProof</c>, with the nonce supplied rather than drawn, so the
    /// specification's test vectors can be reproduced. Callers outside the tests use the
    /// overload that draws its own.
    /// </summary>
    /// <param name="key">The server's private key, <c>k</c>.</param>
    /// <param name="publicKey">The server's public key, <c>B</c>.</param>
    /// <param name="blindedElements">The client's elements, <c>C</c>.</param>
    /// <param name="evaluatedElements">The server's answers, <c>D</c>.</param>
    /// <param name="nonce">The proof nonce, <c>r</c>.</param>
    internal static VoprfProof GenerateProof(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> publicKey,
        IReadOnlyList<byte[]> blindedElements,
        IReadOnlyList<byte[]> evaluatedElements,
        ReadOnlySpan<byte> nonce
    )
    {
        if (
            !TryComputeComposites(
                publicKey,
                blindedElements,
                evaluatedElements,
                key,
                out var m,
                out var z
            )
        )
        {
            throw new VoprfException(
                "Cannot prove over these elements: one of them is not a valid ristretto255 encoding."
            );
        }

        var t2 = Ristretto.ScalarMultiplyGenerator(nonce);
        var t3 = Ristretto.ScalarMultiply(nonce, m);

        var challenge = Challenge(publicKey, m, z, t2, t3);

        // s = r - c * k
        //
        // c*k has to be zeroed, and is the most dangerous buffer in this file: the proof
        // publishes both c and s, so anyone who recovers these 32 bytes computes the private
        // key outright as c*k * c^-1. It is pinned for the same reason the key itself is -
        // zeroing a buffer the GC may already have copied elsewhere achieves nothing.
        var masked = Ristretto.MultiplyScalars(challenge, key, pinned: true);
        try
        {
            return new VoprfProof(challenge, Ristretto.SubtractScalars(nonce, masked));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masked);
        }
    }

    /// <summary>RFC 9497 <c>GenerateProof</c>, drawing a fresh nonce.</summary>
    /// <remarks>
    /// The nonce must be fresh for every proof and must never be reused across two different
    /// challenges: two proofs sharing a nonce let anyone solve the pair of equations for the
    /// private key.
    /// </remarks>
    public static VoprfProof GenerateProof(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> publicKey,
        IReadOnlyList<byte[]> blindedElements,
        IReadOnlyList<byte[]> evaluatedElements
    )
    {
        var nonce = Ristretto.RandomScalar();
        try
        {
            return GenerateProof(key, publicKey, blindedElements, evaluatedElements, nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    /// <summary>
    /// RFC 9497 <c>VerifyProof</c>. Returns <see langword="false"/> for any failure,
    /// including malformed elements, so that a hostile server cannot distinguish the
    /// reasons.
    /// </summary>
    public static bool VerifyProof(
        ReadOnlySpan<byte> publicKey,
        IReadOnlyList<byte[]> blindedElements,
        IReadOnlyList<byte[]> evaluatedElements,
        VoprfProof proof
    )
    {
        // RFC 9497's DeserializeScalar admits only canonical encodings, and here that is a
        // soundness matter rather than a formality: libsodium's scalar multiplication ignores a
        // scalar's top bit, so without this both s and s + 2^255 verify against the same proof.
        // Nothing in this codebase stores or compares a proof, so a malleable one is presently
        // harmless - but "the proof that verified is the proof the server sent" is the kind of
        // thing a later caller will assume, and it costs two comparisons to make it true.
        if (
            !Ristretto.IsCanonicalScalar(proof.Challenge)
            || !Ristretto.IsCanonicalScalar(proof.Response)
        )
        {
            return false;
        }

        if (
            !TryComputeComposites(
                publicKey,
                blindedElements,
                evaluatedElements,
                key: default,
                out var m,
                out var z
            )
        )
        {
            return false;
        }

        // t2 = s * G + c * B, t3 = s * M + c * Z.
        //
        // A zero c or s makes one of these multiplications yield the identity, which
        // libsodium refuses. An honest proof reaches that with negligible probability, and a
        // crafted one is exactly what should be rejected, so failing closed is correct.
        if (
            !Ristretto.TryScalarMultiplyGenerator(proof.Response, out var sG)
            || !Ristretto.TryScalarMultiply(proof.Challenge, publicKey, out var cB)
            || !Ristretto.TryAdd(sG, cB, out var t2)
            || !Ristretto.TryScalarMultiply(proof.Response, m, out var sM)
            || !Ristretto.TryScalarMultiply(proof.Challenge, z, out var cZ)
            || !Ristretto.TryAdd(sM, cZ, out var t3)
        )
        {
            return false;
        }

        var expected = Challenge(publicKey, m, z, t2, t3);

        return CryptographicOperations.FixedTimeEquals(expected, proof.Challenge);
    }

    /// <summary>
    /// The Fiat-Shamir challenge, identical on both sides:
    /// <c>HashToScalar(framed(B) || framed(M) || framed(Z) || framed(t2) || framed(t3) || "Challenge")</c>.
    /// </summary>
    private static byte[] Challenge(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> m,
        ReadOnlySpan<byte> z,
        ReadOnlySpan<byte> t2,
        ReadOnlySpan<byte> t3
    )
    {
        var transcript = new Transcript()
            .AddFramed(publicKey)
            .AddFramed(m)
            .AddFramed(z)
            .AddFramed(t2)
            .AddFramed(t3)
            .AddRaw("Challenge"u8);

        return VoprfSuite.HashToScalar(transcript.Span);
    }

    /// <summary>
    /// RFC 9497 <c>ComputeComposites</c> and <c>ComputeCompositesFast</c> in one.
    /// </summary>
    /// <remarks>
    /// The two differ only in how <c>Z</c> is reached. A prover passes its private key, and
    /// <c>Z = k * M</c> is then a single multiplication; a verifier passes an empty key,
    /// having none, and <c>Z</c> is accumulated from the evaluated elements instead. Both
    /// hash the identical transcript, which is what makes the results comparable.
    /// </remarks>
    private static bool TryComputeComposites(
        ReadOnlySpan<byte> publicKey,
        IReadOnlyList<byte[]> blindedElements,
        IReadOnlyList<byte[]> evaluatedElements,
        ReadOnlySpan<byte> key,
        out byte[] m,
        out byte[] z
    )
    {
        m = [];
        z = [];

        if (
            blindedElements.Count == 0
            || blindedElements.Count != evaluatedElements.Count
            || blindedElements.Count > ushort.MaxValue
        )
        {
            return false;
        }

        var hasKey = !key.IsEmpty;

        // seed = Hash(framed(Bm) || framed(seedDST))
        var seedTranscript = new Transcript().AddFramed(publicKey).AddFramed(VoprfSuite.SeedDst);
        var seed = VoprfSuite.Hash(seedTranscript.Span);

        byte[]? accumulatedM = null;
        byte[]? accumulatedZ = null;

        for (var i = 0; i < blindedElements.Count; i++)
        {
            var compositeTranscript = new Transcript()
                .AddFramed(seed)
                .AddUInt16((ushort)i)
                .AddFramed(blindedElements[i])
                .AddFramed(evaluatedElements[i])
                .AddRaw("Composite"u8);

            var di = VoprfSuite.HashToScalar(compositeTranscript.Span);

            if (!Ristretto.TryScalarMultiply(di, blindedElements[i], out var termM))
            {
                return false;
            }

            accumulatedM = Accumulate(accumulatedM, termM);
            if (accumulatedM is null)
            {
                return false;
            }

            if (hasKey)
            {
                continue;
            }

            if (!Ristretto.TryScalarMultiply(di, evaluatedElements[i], out var termZ))
            {
                return false;
            }

            accumulatedZ = Accumulate(accumulatedZ, termZ);
            if (accumulatedZ is null)
            {
                return false;
            }
        }

        if (accumulatedM is null)
        {
            return false;
        }

        if (hasKey && !Ristretto.TryScalarMultiply(key, accumulatedM, out accumulatedZ))
        {
            return false;
        }

        if (accumulatedZ is null)
        {
            return false;
        }

        m = accumulatedM;
        z = accumulatedZ;
        return true;
    }

    /// <summary>
    /// Adds a term into a running sum that starts at the identity. The identity is kept
    /// implicit as <see langword="null"/> rather than as its encoding, so that no addition
    /// with the identity is ever handed to libsodium.
    /// </summary>
    private static byte[]? Accumulate(byte[]? running, byte[] term)
    {
        if (running is null)
        {
            return term;
        }

        return Ristretto.TryAdd(running, term, out var sum) ? sum : null;
    }
}
