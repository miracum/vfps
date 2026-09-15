using System.Text;

namespace Vfps.Voprf.Client;

/// <summary>
/// How this client reaches a VOPRF server, and what shape the pseudonyms it returns take.
/// </summary>
/// <remarks>
/// One instance describes one key generation. A deployment migrating between generations
/// configures two clients - see the rotation section of the server's README - because a server
/// holds exactly one key and generations are selected by address.
/// </remarks>
public class VoprfClientOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Voprf";

    /// <summary>The server's address, for example <c>https://voprf-v2:8081</c>.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// The server's public key, base64 or hex encoded, obtained out of band.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes the exchange verifiable, and it is the one setting that cannot be
    /// taken from the server itself. A client that verifies against whatever public key the
    /// server just handed it has verified only that the server can do arithmetic consistently:
    /// a server evaluating under a substituted key would offer the matching public key and every
    /// proof would check out, while quietly producing pseudonyms that single this client's
    /// records out from everyone else's.
    /// </para>
    /// <para>
    /// Put it in configuration next to <see cref="Address"/>, or fetch it once through a channel
    /// the server does not control and compare it across clients. The server logs its public key
    /// at startup so an operator can confirm the two match.
    /// </para>
    /// </remarks>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// The key generation this client expects, matching the server's <c>Key:KeyId</c>. Optional;
    /// when set, an answer from a different generation is refused.
    /// </summary>
    /// <remarks>
    /// A mismatch would fail verification anyway, since the pinned public key belongs to one
    /// generation - but failing on the id first says plainly that the server was re-pointed,
    /// rather than reporting a proof failure that reads like tampering.
    /// </remarks>
    public string ExpectedKeyId { get; set; } = string.Empty;

    /// <summary>
    /// Accept the public key the server reports instead of requiring <see cref="PublicKey"/>.
    /// </summary>
    /// <remarks>
    /// Development only, and it discards the entire point of the verifiable variant - see the
    /// remarks on <see cref="PublicKey"/>. It exists because an ephemeral development key has no
    /// stable value to pin.
    /// </remarks>
    public bool AllowUnpinnedPublicKey { get; set; }

    /// <summary>How the pseudonym's bytes are rendered as text.</summary>
    public PseudonymFormat Format { get; set; } = PseudonymFormat.Base64Url;

    /// <summary>
    /// How many bytes of the protocol output to keep, between
    /// <see cref="MinimumLength"/> and <see cref="VoprfSuite.OutputLength"/>.
    /// </summary>
    /// <remarks>
    /// Truncating a pseudorandom output is sound, the way SHA-512/256 is, and 64 bytes is an
    /// unwieldy 86 characters of base64url. What shrinks with the output is collision
    /// resistance: at <c>n</c> bytes, distinct inputs start colliding around <c>2^(4n)</c> of
    /// them. 16 bytes puts that near 2^64, far beyond any realistic dataset; below 16 is refused.
    /// </remarks>
    public int Length { get; set; } = VoprfSuite.OutputLength;

    /// <summary>
    /// Unicode normalization applied before the input is encoded as UTF-8.
    /// <see langword="null"/> disables it.
    /// </summary>
    /// <remarks>
    /// This is where deployments quietly fail. A name whose accent is one code point and the same
    /// name whose accent is a combining mark render identically, arrive from different platforms,
    /// and are different byte strings - so without normalization one person is filed twice and
    /// nothing detects it. NFC makes the two agree.
    ///
    /// Case and whitespace are deliberately left alone: whether <c>Alice@Example.com</c> is the
    /// same subject as <c>alice@example.com</c> is a question about your data, not about the
    /// cryptography, so fold upstream if you need it.
    /// </remarks>
    public NormalizationForm? Normalization { get; set; } = NormalizationForm.FormC;

    /// <summary>Smallest permitted <see cref="Length"/>.</summary>
    public const int MinimumLength = 16;

    /// <summary>
    /// Throws if these options describe a client that cannot work, or one that would silently
    /// produce pseudonyms nobody can trust.
    /// </summary>
    /// <remarks>
    /// Every one of <see cref="Format"/>, <see cref="Length"/> and
    /// <see cref="Normalization"/> changes the output, so they are a contract between the systems
    /// sharing a store rather than a local preference. Changing one is as disruptive as changing
    /// the key, and unlike the key it leaves no <c>key_id</c> behind to notice it by.
    /// </remarks>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Address))
        {
            throw new VoprfClientConfigurationException($"{SectionName}:Address is empty.");
        }

        if (string.IsNullOrWhiteSpace(PublicKey) && !AllowUnpinnedPublicKey)
        {
            throw new VoprfClientConfigurationException(
                $"{SectionName}:PublicKey is empty. Without a pinned public key there is nothing "
                    + "to verify the server's proofs against, which is the whole of what the "
                    + "verifiable variant provides. Set it, or set "
                    + $"{SectionName}:AllowUnpinnedPublicKey for development."
            );
        }

        if (Length is < MinimumLength or > VoprfSuite.OutputLength)
        {
            throw new VoprfClientConfigurationException(
                $"{SectionName}:Length must be between {MinimumLength} and "
                    + $"{VoprfSuite.OutputLength} bytes; got {Length}."
            );
        }

        if (!Enum.IsDefined(Format))
        {
            throw new VoprfClientConfigurationException(
                $"{SectionName}:Format '{Format}' is not a known pseudonym format."
            );
        }
    }
}

/// <summary>Thrown when <see cref="VoprfClientOptions"/> cannot produce a usable client.</summary>
public sealed class VoprfClientConfigurationException(string message) : Exception(message);
