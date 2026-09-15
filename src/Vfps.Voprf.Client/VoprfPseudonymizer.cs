using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vfps.Voprf.Protos;

namespace Vfps.Voprf.Client;

/// <summary>
/// Turns an original value into a stable pseudonym, without the key holder ever seeing it.
/// </summary>
public interface IVoprfPseudonymizer
{
    /// <summary>Pseudonymizes one value.</summary>
    /// <param name="originalValue">The value to pseudonymize. Must not be empty.</param>
    /// <param name="cancellationToken">Cancels the round trip to the server.</param>
    Task<Pseudonym> PseudonymizeAsync(
        string originalValue,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Pseudonymizes several values in one round trip, under a single proof.
    /// </summary>
    /// <param name="originalValues">The values, whose order the results follow.</param>
    /// <param name="cancellationToken">Cancels the round trip to the server.</param>
    /// <remarks>
    /// Cheaper than the same values one at a time - one request, one proof, one verification -
    /// and the server cannot tell a batch of related values from a batch of unrelated ones.
    /// </remarks>
    Task<IReadOnlyList<Pseudonym>> PseudonymizeAsync(
        IReadOnlyList<string> originalValues,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// The client half of RFC 9497 VOPRF, wrapped so callers see values in and pseudonyms out.
/// </summary>
/// <remarks>
/// <para>
/// The original value never leaves this process. It is hashed into the group and multiplied by a
/// fresh random scalar before anything is sent, so what the server receives is a uniformly random
/// group element: it cannot recover the value, and cannot tell that two requests carried the same
/// one. The server applies its key and proves it did so; this class verifies that proof against
/// the pinned public key, removes the blind, and hashes the result together with the original
/// value.
/// </para>
/// <para>
/// The same value under the same key always gives the same pseudonym, so pseudonyms join across
/// tables and across runs. Different keys give unrelated pseudonyms, so two datasets keyed
/// separately cannot be matched against each other.
/// </para>
/// <para>Instances are thread-safe.</para>
/// </remarks>
public sealed class VoprfPseudonymizer : IVoprfPseudonymizer
{
    private readonly VoprfService.VoprfServiceClient client;
    private readonly VoprfClientOptions options;
    private readonly ILogger<VoprfPseudonymizer> logger;
    private readonly SemaphoreSlim publicKeyLock = new(1, 1);
    private byte[]? pinnedPublicKey;

    /// <summary>Creates a pseudonymizer over a gRPC client.</summary>
    public VoprfPseudonymizer(
        VoprfService.VoprfServiceClient client,
        IOptions<VoprfClientOptions> options,
        ILogger<VoprfPseudonymizer> logger
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        this.client = client;
        this.options = options.Value;
        this.logger = logger;

        this.options.Validate();

        if (!string.IsNullOrWhiteSpace(this.options.PublicKey))
        {
            pinnedPublicKey = DecodePublicKey(this.options.PublicKey);
        }
    }

    /// <inheritdoc/>
    public async Task<Pseudonym> PseudonymizeAsync(
        string originalValue,
        CancellationToken cancellationToken = default
    )
    {
        var pseudonyms = await PseudonymizeAsync([originalValue], cancellationToken)
            .ConfigureAwait(false);

        return pseudonyms[0];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Pseudonym>> PseudonymizeAsync(
        IReadOnlyList<string> originalValues,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(originalValues);

        if (originalValues.Count == 0)
        {
            return [];
        }

        var publicKey = await GetPublicKeyAsync(cancellationToken).ConfigureAwait(false);

        var requests = new VoprfRequest[originalValues.Count];
        try
        {
            var call = new VoprfServiceBlindEvaluateRequest();

            for (var i = 0; i < originalValues.Count; i++)
            {
                var encoded = Encode(originalValues[i]);
                try
                {
                    requests[i] = VoprfClient.Blind(encoded);
                }
                finally
                {
                    // The UTF-8 form of the original value. VoprfRequest keeps its own pinned
                    // copy for finalisation; this one has done its job.
                    CryptographicOperations.ZeroMemory(encoded);
                }

                call.BlindedElements.Add(ByteString.CopyFrom(requests[i].BlindedElement));
            }

            var response = await client
                .BlindEvaluateAsync(call, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            CheckGeneration(response.KeyId);

            if (response.EvaluatedElements.Count != originalValues.Count)
            {
                throw new VoprfException(
                    $"Asked the server to evaluate {originalValues.Count} element(s) and got "
                        + $"{response.EvaluatedElements.Count} back."
                );
            }

            // Verifies the proof over the whole batch before unblinding anything, and throws
            // VoprfVerificationException if the server did not use the pinned key.
            var outputs = VoprfClient.Finalize(
                requests,
                [.. response.EvaluatedElements.Select(element => element.ToByteArray())],
                VoprfProof.Parse(response.Proof.Span),
                publicKey
            );

            var pseudonyms = new Pseudonym[outputs.Length];
            for (var i = 0; i < outputs.Length; i++)
            {
                try
                {
                    pseudonyms[i] = new Pseudonym(
                        Format(outputs[i].AsSpan(0, options.Length)),
                        response.KeyId
                    );
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(outputs[i]);
                }
            }

            return pseudonyms;
        }
        finally
        {
            foreach (var request in requests)
            {
                request?.Dispose();
            }
        }
    }

    /// <summary>
    /// Refuses an answer from a generation this client was not configured for.
    /// </summary>
    private void CheckGeneration(string keyId)
    {
        if (
            string.IsNullOrEmpty(options.ExpectedKeyId)
            || string.Equals(keyId, options.ExpectedKeyId, StringComparison.Ordinal)
        )
        {
            return;
        }

        throw new VoprfException(
            $"The server at {options.Address} answered under key '{keyId}', but this client is "
                + $"configured for '{options.ExpectedKeyId}'. Pseudonyms from the two generations "
                + "are unrelated, so the address was probably re-pointed at another deployment."
        );
    }

    /// <summary>
    /// The pinned public key, or - only when explicitly allowed - one fetched from the server.
    /// </summary>
    private async ValueTask<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken)
    {
        if (pinnedPublicKey is not null)
        {
            return pinnedPublicKey;
        }

        await publicKeyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pinnedPublicKey is not null)
            {
                return pinnedPublicKey;
            }

            var info = await client
                .GetPublicKeyAsync(
                    new VoprfServiceGetPublicKeyRequest(),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);

            CheckGeneration(info.KeyId);

            // Loud, and every time the value could still change: an operator who meant to pin
            // has the value right here, and one who did not is told what they gave up.
            logger.LogWarning(
                "Using the public key {PublicKey} reported by {Address} for key {KeyId}. Nothing "
                    + "was verified by doing so - a server evaluating under a substituted key would "
                    + "report the matching public key and every proof would still check out. Pin "
                    + "this value in {Section}:PublicKey.",
                Convert.ToHexStringLower(info.PublicKey.Span),
                options.Address,
                info.KeyId,
                VoprfClientOptions.SectionName
            );

            pinnedPublicKey = info.PublicKey.ToByteArray();
            return pinnedPublicKey;
        }
        finally
        {
            publicKeyLock.Release();
        }
    }

    /// <summary>
    /// Normalizes the value and encodes it as UTF-8 - the exact bytes the protocol evaluates.
    /// </summary>
    private byte[] Encode(string originalValue)
    {
        ArgumentNullException.ThrowIfNull(originalValue);

        if (originalValue.Length == 0)
        {
            // An empty string is almost always a missing value rather than a subject, and
            // quietly giving it a stable pseudonym hides that: every record missing the field
            // ends up sharing one pseudonym, which then looks like one very busy person.
            throw new ArgumentException(
                "Cannot pseudonymize an empty value.",
                nameof(originalValue)
            );
        }

        var normalized =
            options.Normalization is { } form && !originalValue.IsNormalized(form)
                ? originalValue.Normalize(form)
                : originalValue;

        var encoded = Encoding.UTF8.GetBytes(normalized);

        if (encoded.Length > VoprfSuite.MaxInputLength)
        {
            CryptographicOperations.ZeroMemory(encoded);
            throw new ArgumentException(
                $"The value is {encoded.Length} bytes of UTF-8; the protocol frames lengths as "
                    + $"uint16, so at most {VoprfSuite.MaxInputLength} can be pseudonymized.",
                nameof(originalValue)
            );
        }

        return encoded;
    }

    private string Format(ReadOnlySpan<byte> output) =>
        options.Format switch
        {
            PseudonymFormat.Base64Url => Base64Url.EncodeToString(output),
            PseudonymFormat.Hex => Convert.ToHexStringLower(output),
            _ => throw new VoprfClientConfigurationException(
                $"Unknown pseudonym format '{options.Format}'."
            ),
        };

    private static byte[] DecodePublicKey(string value)
    {
        var trimmed = value.Trim();

        var decoded = new byte[VoprfKeyPair.PublicKeyLength];
        if (
            Convert.TryFromBase64String(trimmed, decoded, out var written)
            && written == VoprfKeyPair.PublicKeyLength
        )
        {
            return decoded;
        }

        try
        {
            var fromHex = Convert.FromHexString(trimmed);
            if (fromHex.Length == VoprfKeyPair.PublicKeyLength)
            {
                return fromHex;
            }
        }
        catch (FormatException)
        {
            // Falls through to the shared error below - the value is neither encoding.
        }

        throw new VoprfClientConfigurationException(
            $"{VoprfClientOptions.SectionName}:PublicKey is not "
                + $"{VoprfKeyPair.PublicKeyLength} bytes of base64 or hex."
        );
    }
}
