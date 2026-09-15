using Vfps.Voprf.Internal;

namespace Vfps.Voprf;

/// <summary>
/// A DLEQ proof: the server's evidence that it applied the key behind its published public
/// key, and not some other one.
/// </summary>
/// <remarks>
/// <para>
/// It is a Chaum-Pedersen proof of discrete logarithm equality - that
/// <c>log_G(pkS) == log_B(Z)</c> for the generator <c>G</c>, the blinded element <c>B</c>,
/// and the evaluated element <c>Z</c> - made non-interactive by Fiat-Shamir. It reveals
/// nothing about the key beyond the public key already published.
/// </para>
/// <para>
/// On the wire it is two scalars, <c>c</c> then <c>s</c>, concatenated:
/// <see cref="Length"/> bytes.
/// </para>
/// </remarks>
public sealed class VoprfProof
{
    /// <summary>Length of a serialised proof in bytes.</summary>
    public const int Length = 2 * VoprfSuite.ScalarLength;

    private readonly byte[] challenge;
    private readonly byte[] response;

    internal VoprfProof(byte[] challenge, byte[] response)
    {
        this.challenge = challenge;
        this.response = response;
    }

    internal ReadOnlySpan<byte> Challenge => challenge;

    internal ReadOnlySpan<byte> Response => response;

    /// <summary>Serialises the proof as <c>c || s</c>.</summary>
    public byte[] ToBytes() => [.. challenge, .. response];

    /// <summary>
    /// Reads a proof from its serialised form.
    /// </summary>
    /// <param name="proof">Exactly <see cref="Length"/> bytes.</param>
    /// <exception cref="VoprfException">The length is wrong.</exception>
    /// <remarks>
    /// The two scalars are not range-checked here. A value at or above the group order, or
    /// zero, cannot make a proof verify - it only makes it fail - so there is nothing to
    /// gain by rejecting it earlier, and
    /// <see cref="VoprfClient.Finalize(VoprfRequest, ReadOnlySpan{byte}, VoprfProof, ReadOnlySpan{byte})"/>
    /// is where the decision belongs.
    /// </remarks>
    public static VoprfProof Parse(ReadOnlySpan<byte> proof)
    {
        if (proof.Length != Length)
        {
            throw new VoprfException($"A proof is {Length} bytes, got {proof.Length}.");
        }

        return new VoprfProof(
            proof[..VoprfSuite.ScalarLength].ToArray(),
            proof[VoprfSuite.ScalarLength..].ToArray()
        );
    }
}
