using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Vfps.Config;

namespace Vfps;

/// <summary>
/// Loads the X.509 certificates that encrypt the Data Protection key ring at rest - see
/// <see cref="DataProtectionConfig"/> for why it needs encrypting at all.
///
/// Kept out of Program.cs as a pure function over the configuration so it can be unit-tested.
///
/// Every failure here throws rather than degrading to an unencrypted key ring. A typo in a path
/// that silently fell back to plaintext would be the worst outcome available: the deployment
/// would look configured, log nothing alarming, and protect nothing.
/// </summary>
internal static class DataProtectionCertificateLoader
{
    /// <summary>
    /// Loads every configured certificate, in order. The caller protects new keys with the first
    /// and accepts all of them for decryption - see <see cref="DataProtectionConfig.Certificates"/>.
    /// </summary>
    /// <exception cref="DataProtectionCertificateException">
    /// Any entry is missing a path, points at a file that doesn't exist or can't be parsed, or
    /// resolves to a certificate without a usable private key.
    /// </exception>
    internal static IReadOnlyList<X509Certificate2> Load(
        IReadOnlyList<DataProtectionCertificateConfig> configured
    )
    {
        var certificates = new List<X509Certificate2>(configured.Count);

        try
        {
            for (var index = 0; index < configured.Count; index++)
            {
                certificates.Add(LoadOne(configured[index], index));
            }
        }
        catch
        {
            // Everything loaded so far holds an unmanaged key handle. Startup is about to fail
            // either way, but leaking them on the way out would make the failure harder to read
            // in any host that logs finalizer-thread noise.
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }

            throw;
        }

        return certificates;
    }

    private static X509Certificate2 LoadOne(DataProtectionCertificateConfig config, int index)
    {
        // The configuration key rather than just the file path: with several certificates
        // configured for a rotation, "the second one" is the part that identifies which entry to
        // go and fix, and an empty Path has no file name to name at all.
        var setting = $"DataProtection:Certificates:{index}";

        if (string.IsNullOrWhiteSpace(config.Path))
        {
            throw new DataProtectionCertificateException(
                $"{setting}:Path is not set. It must point at a PKCS#12 archive, or at a PEM "
                    + $"certificate alongside {setting}:KeyPath."
            );
        }

        if (!File.Exists(config.Path))
        {
            throw new DataProtectionCertificateException(
                $"{setting}:Path points at '{config.Path}', which does not exist or is not "
                    + "readable by the application."
            );
        }

        var certificate = ReadCertificate(config, setting);

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new DataProtectionCertificateException(
                $"{setting} ('{config.Path}') carries no private key. The key ring is decrypted "
                    + "on every startup, so a public certificate alone would encrypt keys that "
                    + "this deployment could never read back."
            );
        }

        return certificate;
    }

    private static X509Certificate2 ReadCertificate(
        DataProtectionCertificateConfig config,
        string setting
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(config.KeyPath))
            {
                return X509CertificateLoader.LoadPkcs12FromFile(
                    config.Path,
                    string.IsNullOrEmpty(config.Password) ? null : config.Password
                );
            }

            if (!File.Exists(config.KeyPath))
            {
                throw new DataProtectionCertificateException(
                    $"{setting}:KeyPath points at '{config.KeyPath}', which does not exist or is "
                        + "not readable by the application."
                );
            }

            return string.IsNullOrEmpty(config.Password)
                ? X509Certificate2.CreateFromPemFile(config.Path, config.KeyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(
                    config.Path,
                    config.Password,
                    config.KeyPath
                );
        }
        catch (CryptographicException ex)
        {
            // Overwhelmingly a wrong-or-missing password, or a PEM file handed to the PKCS#12
            // loader (or the reverse). The inner exception's own message says which, but on its
            // own it never says which setting produced it.
            throw new DataProtectionCertificateException(
                $"{setting} ('{config.Path}') could not be read: {ex.Message} Check that the file "
                    + $"is a {(string.IsNullOrWhiteSpace(config.KeyPath) ? "PKCS#12 archive" : "PEM certificate")} "
                    + $"and that {setting}:Password matches it.",
                ex
            );
        }
    }
}

/// <summary>
/// Thrown when a configured Data Protection key-protection certificate can't be loaded. Always
/// fatal at startup - see <see cref="DataProtectionCertificateLoader"/>.
/// </summary>
public class DataProtectionCertificateException : Exception
{
    public DataProtectionCertificateException(string message)
        : base(message) { }

    public DataProtectionCertificateException(string message, Exception innerException)
        : base(message, innerException) { }
}
