using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Vfps;
using Vfps.Config;

namespace Vfps.Tests;

/// <summary>
/// A misconfigured key-protection certificate has to stop startup rather than quietly leave the
/// Data Protection key ring in plaintext - see <see cref="DataProtectionCertificateLoader"/>. Most
/// of what's asserted here is therefore about failing, and about the failure naming the setting
/// that caused it.
/// </summary>
public sealed class DataProtectionCertificateLoaderTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("vfps-dp-certs");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void Load_WithNoCertificatesConfigured_ReturnsEmpty()
    {
        // The default, and the case that leaves the key ring unencrypted: it's Program.cs that
        // decides to warn about it, so the loader itself must treat it as unremarkable.
        DataProtectionCertificateLoader.Load([]).Should().BeEmpty();
    }

    [Fact]
    public void Load_FromPkcs12WithoutPassword_ReturnsCertificateWithPrivateKey()
    {
        var path = WritePkcs12("keyring.pfx", password: null);

        var loaded = DataProtectionCertificateLoader.Load([new() { Path = path }]);

        loaded.Should().ContainSingle().Which.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public void Load_FromPkcs12WithPassword_ReturnsCertificateWithPrivateKey()
    {
        var path = WritePkcs12("keyring.pfx", Password);

        var loaded = DataProtectionCertificateLoader.Load([
            new() { Path = path, Password = Password },
        ]);

        loaded.Should().ContainSingle().Which.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public void Load_FromPemPair_ReturnsCertificateWithPrivateKey()
    {
        // The shape cert-manager and most Kubernetes tooling produce (tls.crt + tls.key), so this
        // is the path the Helm chart's own wiring takes.
        var (certPath, keyPath) = WritePem("tls.crt", "tls.key", password: null);

        var loaded = DataProtectionCertificateLoader.Load([
            new() { Path = certPath, KeyPath = keyPath },
        ]);

        loaded.Should().ContainSingle().Which.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public void Load_FromPemPairWithEncryptedKey_ReturnsCertificateWithPrivateKey()
    {
        var (certPath, keyPath) = WritePem("tls.crt", "tls.key", Password);

        var loaded = DataProtectionCertificateLoader.Load([
            new()
            {
                Path = certPath,
                KeyPath = keyPath,
                Password = Password,
            },
        ]);

        loaded.Should().ContainSingle().Which.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public void Load_WithSeveralCertificates_PreservesOrder()
    {
        // Order is load-bearing, not cosmetic: Program.cs protects new keys with the first entry
        // and accepts every entry for decryption, which is what makes rotation possible.
        var first = WritePkcs12("first.pfx", password: null);
        var second = WritePkcs12("second.pfx", password: null);

        var loaded = DataProtectionCertificateLoader.Load([
            new() { Path = first },
            new() { Path = second },
        ]);

        loaded.Should().HaveCount(2);
        loaded[0].Thumbprint.Should().NotBe(loaded[1].Thumbprint);
    }

    [Fact]
    public void Load_WithoutAPath_Throws()
    {
        var act = () => DataProtectionCertificateLoader.Load([new()]);

        act.Should()
            .Throw<DataProtectionCertificateException>()
            .WithMessage("*DataProtection:Certificates:0:Path*");
    }

    [Fact]
    public void Load_WithAMissingFile_Throws()
    {
        var missing = Path.Combine(_directory.FullName, "nope.pfx");

        var act = () => DataProtectionCertificateLoader.Load([new() { Path = missing }]);

        act.Should()
            .Throw<DataProtectionCertificateException>()
            .WithMessage($"*{missing}*does not exist*");
    }

    [Fact]
    public void Load_WithAMissingKeyFile_NamesTheKeyPathSetting()
    {
        var (certPath, keyPath) = WritePem("tls.crt", "tls.key", password: null);
        File.Delete(keyPath);

        var act = () =>
            DataProtectionCertificateLoader.Load([new() { Path = certPath, KeyPath = keyPath }]);

        act.Should()
            .Throw<DataProtectionCertificateException>()
            .WithMessage("*DataProtection:Certificates:0:KeyPath*");
    }

    [Fact]
    public void Load_WithTheWrongPassword_Throws()
    {
        var path = WritePkcs12("keyring.pfx", Password);

        var act = () =>
            DataProtectionCertificateLoader.Load([
                new() { Path = path, Password = "not the password" },
            ]);

        act.Should()
            .Throw<DataProtectionCertificateException>()
            .WithMessage("*DataProtection:Certificates:0*");
    }

    [Fact]
    public void Load_WithAPublicCertificateOnly_Throws()
    {
        // Encrypting the ring with a certificate whose private key is absent would produce a
        // deployment that writes keys it can never read back on the next restart.
        using var certificate = CreateSelfSigned();
        var path = Path.Combine(_directory.FullName, "public-only.crt");
        File.WriteAllText(path, certificate.ExportCertificatePem());

        var act = () => DataProtectionCertificateLoader.Load([new() { Path = path }]);

        act.Should()
            .Throw<DataProtectionCertificateException>()
            .WithMessage("*DataProtection:Certificates:0*");
    }

    [Fact]
    public void Load_WhenALaterEntryFails_NamesThatEntrysIndex()
    {
        var valid = WritePkcs12("first.pfx", password: null);

        var act = () => DataProtectionCertificateLoader.Load([new() { Path = valid }, new()]);

        act.Should()
            .Throw<DataProtectionCertificateException>()
            .WithMessage("*DataProtection:Certificates:1*");
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=vfps-dataprotection-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30)
        );
    }

    private string WritePkcs12(string fileName, string? password)
    {
        using var certificate = CreateSelfSigned();
        var path = Path.Combine(_directory.FullName, fileName);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));
        return path;
    }

    private (string CertificatePath, string KeyPath) WritePem(
        string certificateFileName,
        string keyFileName,
        string? password
    )
    {
        using var certificate = CreateSelfSigned();
        using var key = certificate.GetRSAPrivateKey()!;

        var certificatePath = Path.Combine(_directory.FullName, certificateFileName);
        var keyPath = Path.Combine(_directory.FullName, keyFileName);

        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(
            keyPath,
            password is null
                ? key.ExportPkcs8PrivateKeyPem()
                : key.ExportEncryptedPkcs8PrivateKeyPem(
                    password,
                    new PbeParameters(
                        PbeEncryptionAlgorithm.Aes256Cbc,
                        HashAlgorithmName.SHA256,
                        100_000
                    )
                )
        );

        return (certificatePath, keyPath);
    }
}
