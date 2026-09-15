using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Vfps.Voprf.Server.Config;

namespace Vfps.Voprf.Server.Keys;

/// <summary>Holds the key pair this server evaluates under.</summary>
public interface IVoprfKeyProvider : IDisposable
{
    /// <summary>The key pair. Borrowed - callers must not dispose it.</summary>
    VoprfKeyPair KeyPair { get; }

    /// <summary>The generation this key belongs to, returned with every answer.</summary>
    string KeyId { get; }
}

/// <summary>
/// Resolves the key once at startup, from whichever source <see cref="KeyConfig"/> names.
/// </summary>
/// <remarks>
/// Registered as a singleton: the key is loaded, the file handle closed, and the material kept
/// in a pinned buffer inside <see cref="VoprfKeyPair"/> for the lifetime of the process. Loading
/// per request would mean the secret repeatedly crossing into managed memory for no benefit.
/// </remarks>
public sealed class VoprfKeyProvider : IVoprfKeyProvider
{
    private readonly ILogger<VoprfKeyProvider> logger;

    /// <summary>Loads the key described by <paramref name="options"/>.</summary>
    public VoprfKeyProvider(IOptions<VoprfServerConfig> options, ILogger<VoprfKeyProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.logger = logger;

        var config = options.Value.Key;
        KeyId = config.KeyId;
        KeyPair = Load(config);

        // The public key is not a secret - clients need it to verify - and having it in the log
        // is what lets an operator confirm that the key a client pinned is the one in use.
        logger.LogInformation(
            "Loaded VOPRF key {KeyId} from {Source}; public key {PublicKey}",
            KeyId,
            config.Source,
            Convert.ToHexStringLower(KeyPair.PublicKey)
        );
    }

    /// <inheritdoc/>
    public VoprfKeyPair KeyPair { get; }

    /// <inheritdoc/>
    public string KeyId { get; }

    /// <inheritdoc/>
    public void Dispose() => KeyPair.Dispose();

    private VoprfKeyPair Load(KeyConfig config)
    {
        switch (config.Source)
        {
            case KeySource.Ephemeral:
                // HardeningGuard refuses this with hardening on, so reaching here means a
                // developer asked for it deliberately. Say so anyway: the symptom of forgetting
                // is that yesterday's pseudonyms stop matching today's.
                logger.LogWarning(
                    "Using an ephemeral VOPRF key. It is regenerated on every restart, so every "
                        + "pseudonym issued under it becomes unreproducible when this process exits."
                );
                return VoprfKeyPair.Generate();

            case KeySource.File:
            {
                var material = ReadFile(config.FilePath);
                byte[]? decoded = null;
                try
                {
                    decoded = DecodeKey(material, config.FilePath);
                    return VoprfKeyPair.Import(decoded);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(material);

                    // DecodeKey hands back the file's own bytes for a raw key and a fresh buffer
                    // for a base64 one, so the second case needs clearing separately.
                    if (decoded is not null && !ReferenceEquals(decoded, material))
                    {
                        CryptographicOperations.ZeroMemory(decoded);
                    }
                }
            }

            case KeySource.Base64:
            {
                var material = DecodeBase64(
                    config.Base64,
                    $"{VoprfServerConfig.SectionName}:Key:Base64"
                );
                try
                {
                    return VoprfKeyPair.Import(material);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(material);
                }
            }

            case KeySource.Seed:
            {
                var seed = string.IsNullOrWhiteSpace(config.FilePath)
                    ? DecodeBase64(config.Base64, $"{VoprfServerConfig.SectionName}:Key:Base64")
                    : ReadSeedFile(config.FilePath);
                try
                {
                    return VoprfKeyPair.Derive(
                        seed,
                        System.Text.Encoding.UTF8.GetBytes(config.KeyInfo)
                    );
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(seed);
                }
            }

            default:
                throw new InvalidOperationException($"Unknown key source '{config.Source}'.");
        }
    }

    private static byte[] ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{VoprfServerConfig.SectionName}:Key:FilePath points at '{path}', which does not exist."
            );
        }

        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// Accepts either the raw 32 bytes or their base64 encoding. A secret written with
    /// <c>openssl rand -base64 32</c>, mounted from a Kubernetes secret, or produced by
    /// <c>VoprfKeyPair.ExportPrivateKey</c> all arrive in one of those two shapes, and guessing
    /// wrong is a startup failure rather than a silently different key.
    /// </summary>
    private static byte[] DecodeKey(byte[] material, string path)
    {
        if (material.Length == VoprfKeyPair.PrivateKeyLength)
        {
            return material;
        }

        // Decoded exactly once, into a buffer the caller zeroes. Probing with a throwaway array
        // and then decoding again - the obvious shape - would leave a second copy of the private
        // key on the heap that nobody owns and nothing clears.
        //
        // The base64 text itself cannot be erased: strings are immutable, so a key that arrives
        // base64 encoded lingers until the GC reclaims it. A key file of the raw 32 bytes avoids
        // that path entirely, which is one more reason to prefer it.
        var decoded = new byte[VoprfKeyPair.PrivateKeyLength];
        var text = System.Text.Encoding.UTF8.GetString(material).Trim();

        if (
            Convert.TryFromBase64String(text, decoded, out var written)
            && written == VoprfKeyPair.PrivateKeyLength
        )
        {
            return decoded;
        }

        CryptographicOperations.ZeroMemory(decoded);

        throw new InvalidOperationException(
            $"The key file '{path}' holds neither {VoprfKeyPair.PrivateKeyLength} raw bytes nor "
                + $"{VoprfKeyPair.PrivateKeyLength} bytes of base64."
        );
    }

    /// <summary>
    /// A seed has no required length, so only the base64 shape is unambiguous here: raw bytes
    /// are returned as they are.
    /// </summary>
    private static byte[] ReadSeedFile(string path)
    {
        var material = ReadFile(path);
        var text = System.Text.Encoding.UTF8.GetString(material).Trim();

        // As in DecodeKey: decoded once into a buffer that is either returned or zeroed, rather
        // than probed into a throwaway array and decoded again.
        var decoded = new byte[((text.Length / 4) + 1) * 3];
        if (text.Length > 0 && Convert.TryFromBase64String(text, decoded, out var written))
        {
            CryptographicOperations.ZeroMemory(material);

            var seed = decoded.AsSpan(0, written).ToArray();
            CryptographicOperations.ZeroMemory(decoded);
            return seed;
        }

        CryptographicOperations.ZeroMemory(decoded);
        return material;
    }

    private static byte[] DecodeBase64(string value, string settingName)
    {
        try
        {
            return Convert.FromBase64String(value.Trim());
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"{settingName} is not valid base64.", exception);
        }
    }
}
