using Vfps.Voprf.Internal;

namespace Vfps.Voprf;

/// <summary>
/// The side that holds the key: it answers blinded elements and proves it used the key it
/// published, and never sees the inputs.
/// </summary>
/// <remarks>
/// Evaluation is a single scalar multiplication and leaks nothing about the input. What it
/// does reveal is traffic - who asked, when, and how often. A server that answers without
/// limit is also an oracle for anyone who can reach it, so rate limit and authenticate
/// callers as you would any other use of the key.
/// </remarks>
public static class VoprfServer
{
    /// <summary>
    /// Applies the key to one blinded element and proves it. RFC 9497 <c>BlindEvaluate</c>.
    /// </summary>
    /// <param name="keyPair">The server's key pair.</param>
    /// <param name="blindedElement">The element from <see cref="VoprfRequest.BlindedElement"/>.</param>
    /// <returns>The evaluated element and the proof, both for the client.</returns>
    /// <exception cref="VoprfException">The element is not a valid ristretto255 encoding.</exception>
    public static (byte[] EvaluatedElement, VoprfProof Proof) BlindEvaluate(
        VoprfKeyPair keyPair,
        ReadOnlySpan<byte> blindedElement
    )
    {
        var (evaluated, proof) = BlindEvaluate(keyPair, [blindedElement.ToArray()]);
        return (evaluated[0], proof);
    }

    /// <summary>
    /// Applies the key to a batch of blinded elements under a single proof covering all of
    /// them.
    /// </summary>
    /// <param name="keyPair">The server's key pair.</param>
    /// <param name="blindedElements">The elements, in the order the client sent them.</param>
    /// <remarks>
    /// The proof is over a random linear combination of the batch, so it costs one proof
    /// rather than one per element - but it is all-or-nothing: the client learns that every
    /// element was answered with the same key, or that some element was not, never which.
    /// Order matters, and the client must verify against the same sequence it sent.
    /// </remarks>
    public static (byte[][] EvaluatedElements, VoprfProof Proof) BlindEvaluate(
        VoprfKeyPair keyPair,
        IReadOnlyList<byte[]> blindedElements
    ) => BlindEvaluate(keyPair, blindedElements, nonce: default);

    internal static (byte[][] EvaluatedElements, VoprfProof Proof) BlindEvaluate(
        VoprfKeyPair keyPair,
        IReadOnlyList<byte[]> blindedElements,
        ReadOnlySpan<byte> nonce
    )
    {
        ArgumentNullException.ThrowIfNull(keyPair);
        ArgumentNullException.ThrowIfNull(blindedElements);

        if (blindedElements.Count == 0)
        {
            throw new ArgumentException("Cannot evaluate an empty batch.", nameof(blindedElements));
        }

        var evaluated = new byte[blindedElements.Count][];
        for (var i = 0; i < blindedElements.Count; i++)
        {
            var element = blindedElements[i];

            // Reject non-canonical encodings and the identity before touching the key. The
            // identity would make the answer independent of the key, which is not something
            // to sign a proof over.
            if (!Ristretto.IsValidElement(element))
            {
                throw new VoprfException(
                    $"Blinded element {i} is not a canonical encoding of a non-identity "
                        + "ristretto255 element."
                );
            }

            evaluated[i] = Ristretto.ScalarMultiply(keyPair.PrivateKey, element);
        }

        var proof = nonce.IsEmpty
            ? Dleq.GenerateProof(keyPair.PrivateKey, keyPair.PublicKey, blindedElements, evaluated)
            : Dleq.GenerateProof(
                keyPair.PrivateKey,
                keyPair.PublicKey,
                blindedElements,
                evaluated,
                nonce
            );

        return (evaluated, proof);
    }

    /// <summary>
    /// Computes the output directly, for an entity that holds both the key and the input.
    /// RFC 9497 <c>Evaluate</c>.
    /// </summary>
    /// <param name="keyPair">The server's key pair.</param>
    /// <param name="input">At most <see cref="VoprfSuite.MaxInputLength"/> bytes.</param>
    /// <returns>The <see cref="VoprfSuite.OutputLength"/>-byte output.</returns>
    /// <remarks>
    /// <para>
    /// This is byte-for-byte what the blinded exchange produces for the same input and key,
    /// because the blind cancels exactly. It is the shortcut for a deployment where one
    /// process legitimately holds both - and in that arrangement the protocol buys nothing
    /// over an HMAC, since neither the blinding nor the proof has anyone to protect against.
    /// </para>
    /// <para>
    /// The reason to use it anyway is that the key can move out later without changing a
    /// single stored value: the same outputs come back from
    /// <see cref="BlindEvaluate(VoprfKeyPair, ReadOnlySpan{byte})"/> and
    /// <see cref="VoprfClient"/>, so the move is a deployment change rather than a data
    /// migration.
    /// </para>
    /// </remarks>
    public static byte[] Evaluate(VoprfKeyPair keyPair, ReadOnlySpan<byte> input)
    {
        ArgumentNullException.ThrowIfNull(keyPair);
        VoprfSuite.CheckInputLength(input);

        var element = VoprfSuite.HashToGroup(input);
        var evaluated = Ristretto.ScalarMultiply(keyPair.PrivateKey, element);

        return VoprfSuite.FinalizeHash(input, evaluated);
    }
}
