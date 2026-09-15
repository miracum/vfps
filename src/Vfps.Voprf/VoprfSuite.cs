using System.Security.Cryptography;
using System.Text;
using Vfps.Voprf.Internal;

namespace Vfps.Voprf;

/// <summary>
/// The ciphersuite: VOPRF(ristretto255, SHA-512), RFC 9497 in verifiable mode.
/// </summary>
/// <remarks>
/// <para>
/// Everything that binds an output to this particular protocol goes through here. RFC 9497
/// derives a <c>contextString</c> from the mode and the ciphersuite identifier and stirs it
/// into every hash, so that an element or a proof produced for one mode cannot be replayed
/// into another - a VOPRF evaluation is not a valid base-OPRF evaluation, even under the
/// same key.
/// </para>
/// <para>
/// Nothing here is configurable. Each of these values is part of the wire contract with any
/// other RFC 9497 implementation, and with every output already in your store.
/// </para>
/// </remarks>
public static class VoprfSuite
{
    /// <summary>The ciphersuite identifier, as it appears in RFC 9497 section 4.1.</summary>
    public const string Identifier = "ristretto255-SHA512";

    /// <summary>Length of a protocol output in bytes (<c>Nh</c>, SHA-512's output).</summary>
    public const int OutputLength = 64;

    /// <summary>Length of a serialised element in bytes (<c>Ne</c>).</summary>
    public const int ElementLength = Ristretto.ElementBytes;

    /// <summary>Length of a serialised scalar in bytes (<c>Ns</c>).</summary>
    public const int ScalarLength = Ristretto.ScalarBytes;

    /// <summary>
    /// Largest input the protocol can carry. RFC 9497 frames input lengths as
    /// <c>uint16</c>, so longer values cannot be hashed unambiguously.
    /// </summary>
    public const int MaxInputLength = ushort.MaxValue;

    /// <summary>RFC 9497 <c>modeVOPRF</c>.</summary>
    private const byte ModeVoprf = 0x01;

    /// <summary>
    /// <c>contextString = "OPRFV1-" || I2OSP(mode, 1) || "-" || identifier</c>.
    /// </summary>
    private static readonly byte[] ContextString =
    [
        .. "OPRFV1-"u8,
        ModeVoprf,
        .. "-"u8,
        .. Encoding.ASCII.GetBytes(Identifier),
    ];

    private static readonly byte[] HashToGroupDst = Dst("HashToGroup-");
    private static readonly byte[] HashToScalarDst = Dst("HashToScalar-");

    // No hyphen: RFC 9497 section 3.2 spells this one "DeriveKeyPair" || contextString.
    private static readonly byte[] DeriveKeyPairDst = Dst("DeriveKeyPair");

    internal static readonly byte[] SeedDst = Dst("Seed-");

    private static byte[] Dst(string prefix) =>
        [.. Encoding.ASCII.GetBytes(prefix), .. ContextString];

    /// <summary>
    /// Maps an input onto the group. RFC 9497 <c>HashToGroup</c>, which for this suite is
    /// <c>hash_to_ristretto255</c> with <c>expand_message_xmd</c> over SHA-512.
    /// </summary>
    internal static byte[] HashToGroup(ReadOnlySpan<byte> input)
    {
        // The expansion is a deterministic function of the identifier, so it is as good as a
        // fingerprint of it: anyone holding these 64 bytes can confirm a guessed input.
        var uniform = ExpandMessage.Xmd(input, HashToGroupDst, Ristretto.WideBytes);
        try
        {
            return Ristretto.HashToGroupMap(uniform);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(uniform);
        }
    }

    /// <summary>
    /// Hashes a transcript to a scalar. RFC 9497 <c>HashToScalar</c>: expand to 64 bytes,
    /// read them as a little-endian integer, and reduce modulo the group order.
    /// </summary>
    internal static byte[] HashToScalar(ReadOnlySpan<byte> input) =>
        Ristretto.ScalarFromWide(ExpandMessage.Xmd(input, HashToScalarDst, Ristretto.WideBytes));

    /// <summary>
    /// The <c>HashToScalar</c> used by <c>DeriveKeyPair</c>, which takes its own domain
    /// separation tag so that a derived key cannot collide with a protocol scalar.
    /// </summary>
    internal static byte[] HashToScalarForKeyDerivation(ReadOnlySpan<byte> input)
    {
        // These 64 bytes reduce to the private key, so they are the private key in all but
        // encoding. The result is pinned because the caller is about to hold it as the key.
        var uniform = ExpandMessage.Xmd(input, DeriveKeyPairDst, Ristretto.WideBytes);
        try
        {
            return Ristretto.ScalarFromWide(uniform, pinned: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(uniform);
        }
    }

    /// <summary>
    /// The final hash, shared by the client's <c>Finalize</c> and the server's
    /// <c>Evaluate</c>: <c>Hash(I2OSP(len(input), 2) || input ||
    /// I2OSP(len(element), 2) || element || "Finalize")</c>.
    /// </summary>
    /// <remarks>
    /// Hashing the input back in alongside the group element is what makes the output a
    /// pseudorandom function of the input rather than merely a function of the element, and
    /// it is why the client must keep the input it blinded.
    /// </remarks>
    internal static byte[] FinalizeHash(
        ReadOnlySpan<byte> input,
        ReadOnlySpan<byte> unblindedElement
    )
    {
        // Sized exactly so the writer never grows: a grown buffer abandons the old array with
        // the identifier still in it, and Clear cannot reach that one.
        const int FinalizeLabelLength = 8; // "Finalize"
        var transcript = new Transcript(
            (2 * sizeof(ushort)) + input.Length + unblindedElement.Length + FinalizeLabelLength
        );

        try
        {
            transcript.AddFramed(input).AddFramed(unblindedElement).AddRaw("Finalize"u8);

            return SHA512.HashData(transcript.Span);
        }
        finally
        {
            // This transcript frames the identifier itself, unlike the challenge and composite
            // transcripts, which carry only group elements and hashes that are public anyway.
            transcript.Clear();
        }
    }

    /// <summary>The hash used to seed a batch's random linear combination.</summary>
    internal static byte[] Hash(ReadOnlySpan<byte> input) => SHA512.HashData(input);

    internal static void CheckInputLength(ReadOnlySpan<byte> input) =>
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input.Length, MaxInputLength, nameof(input));
}
