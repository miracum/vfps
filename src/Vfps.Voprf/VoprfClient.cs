using System.Security.Cryptography;
using Vfps.Voprf.Internal;

namespace Vfps.Voprf;

/// <summary>
/// The side that holds the input: it blinds, sends, verifies, and unblinds, and never sees
/// the key.
/// </summary>
public static class VoprfClient
{
    /// <summary>
    /// Hashes the input into the group and blinds it with a fresh random scalar.
    /// RFC 9497 <c>Blind</c>.
    /// </summary>
    /// <param name="input">At most <see cref="VoprfSuite.MaxInputLength"/> bytes.</param>
    /// <returns>
    /// The request. Dispose it once finalised; it carries the blinding scalar and a copy of
    /// the input.
    /// </returns>
    public static VoprfRequest Blind(ReadOnlySpan<byte> input) => Blind(input, blind: default);

    internal static VoprfRequest Blind(ReadOnlySpan<byte> input, ReadOnlySpan<byte> blind)
    {
        VoprfSuite.CheckInputLength(input);

        byte[] blindScalar;
        if (blind.IsEmpty)
        {
            blindScalar = Ristretto.RandomScalar();
        }
        else
        {
            blindScalar = GC.AllocateArray<byte>(VoprfSuite.ScalarLength, pinned: true);
            blind.CopyTo(blindScalar);
        }

        var retained = GC.AllocateArray<byte>(input.Length, pinned: true);
        input.CopyTo(retained);

        var inputElement = VoprfSuite.HashToGroup(input);
        var blindedElement = Ristretto.ScalarMultiply(blindScalar, inputElement);

        return new VoprfRequest(blindScalar, retained, blindedElement);
    }

    /// <summary>
    /// Verifies the server's proof and unblinds its answer. RFC 9497 <c>Finalize</c>.
    /// </summary>
    /// <param name="request">The request returned by <see cref="Blind(ReadOnlySpan{byte})"/>.</param>
    /// <param name="evaluatedElement">The element the server returned.</param>
    /// <param name="proof">The proof the server returned.</param>
    /// <param name="publicKey">
    /// The server's public key, obtained out of band. Accepting one the server supplies
    /// alongside the answer verifies nothing.
    /// </param>
    /// <returns>The <see cref="VoprfSuite.OutputLength"/>-byte output.</returns>
    /// <exception cref="VoprfVerificationException">The proof does not verify.</exception>
    public static byte[] Finalize(
        VoprfRequest request,
        ReadOnlySpan<byte> evaluatedElement,
        VoprfProof proof,
        ReadOnlySpan<byte> publicKey
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return Finalize([request], [evaluatedElement.ToArray()], proof, publicKey)[0];
    }

    /// <summary>
    /// Verifies one batched proof and unblinds every answer under it.
    /// </summary>
    /// <param name="requests">The requests, in the order they were sent.</param>
    /// <param name="evaluatedElements">The server's answers, in the same order.</param>
    /// <param name="proof">The single proof covering the batch.</param>
    /// <param name="publicKey">The server's public key, obtained out of band.</param>
    /// <exception cref="VoprfVerificationException">
    /// The proof does not verify. Nothing in the batch is returned: the proof covers the
    /// whole of it, so a failure says some element was not answered under the published key
    /// without saying which.
    /// </exception>
    public static byte[][] Finalize(
        IReadOnlyList<VoprfRequest> requests,
        IReadOnlyList<byte[]> evaluatedElements,
        VoprfProof proof,
        ReadOnlySpan<byte> publicKey
    )
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(evaluatedElements);
        ArgumentNullException.ThrowIfNull(proof);

        if (requests.Count == 0)
        {
            throw new ArgumentException("Cannot finalise an empty batch.", nameof(requests));
        }

        if (requests.Count != evaluatedElements.Count)
        {
            throw new ArgumentException(
                $"Got {evaluatedElements.Count} answers for {requests.Count} requests.",
                nameof(evaluatedElements)
            );
        }

        if (publicKey.Length != VoprfKeyPair.PublicKeyLength)
        {
            throw new ArgumentException(
                $"A public key is {VoprfKeyPair.PublicKeyLength} bytes, got {publicKey.Length}.",
                nameof(publicKey)
            );
        }

        var blindedElements = new byte[requests.Count][];
        for (var i = 0; i < requests.Count; i++)
        {
            blindedElements[i] = requests[i].BlindedElement;
        }

        // Verify before unblinding. An unverified answer is not a weaker output, it is a
        // value of unknown provenance, and there is nothing useful to do with it.
        if (!Dleq.VerifyProof(publicKey, blindedElements, evaluatedElements, proof))
        {
            throw new VoprfVerificationException();
        }

        var outputs = new byte[requests.Count][];
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var inverse = Ristretto.ScalarInverse(request.Blind);
            byte[]? unblinded = null;
            try
            {
                // N = blind^-1 * evaluatedElement, which is what the server would have
                // computed had it known the input.
                unblinded = Ristretto.ScalarMultiply(inverse, evaluatedElements[i]);
                outputs[i] = VoprfSuite.FinalizeHash(request.Input, unblinded);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(inverse);

                // N is the output one hash short of the pseudonym, and unlike the pseudonym it
                // is the same value the key holder computes - so it links this client's request
                // to that evaluation for anyone who recovers both.
                if (unblinded is not null)
                {
                    CryptographicOperations.ZeroMemory(unblinded);
                }
            }
        }

        return outputs;
    }
}
