using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Vfps.Tests;

/// <summary>
/// The Data Protection key ring is persisted to the same database as the pseudonyms, so whether
/// its rows are encrypted is the whole point of the certificate wiring in Program.cs. These tests
/// assert against the bytes that actually land in the table rather than against the builder calls,
/// because the failure mode being guarded here is a deployment that looks configured and stores
/// plaintext anyway.
/// </summary>
public sealed class DataProtectionKeyRingTests : IDisposable
{
    /// <summary>
    /// The comment ASP.NET Core writes into the key XML next to an unprotected master key. Its
    /// presence is the clearest available signal that a row is readable by anyone holding the
    /// database.
    /// </summary>
    private const string UnencryptedMarker = "unencrypted form";

    private readonly SqliteConnection _connection = new("Filename=:memory:");

    public DataProtectionKeyRingTests()
    {
        _connection.Open();

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void WithoutACertificate_KeysArePersistedInPlaintext()
    {
        // The pre-fix behavior, pinned deliberately: it's what the startup warning in Program.cs
        // describes, and having it asserted here is what makes the contrast below meaningful.
        using var services = BuildProvider();

        Protect(services, "anything");

        StoredKeyXml().Should().Contain(UnencryptedMarker);
    }

    [Fact]
    public void WithACertificate_KeysArePersistedEncrypted()
    {
        using var certificate = CreateSelfSigned();
        using var services = BuildProvider(certificate);

        Protect(services, "anything");

        // "encryptedSecret" is the element ASP.NET Core writes in place of the plaintext
        // "masterKey" one once an IXmlEncryptor is configured, so asserting on both directions
        // pins the actual substitution rather than just the absence of a warning comment.
        var xml = StoredKeyXml();
        xml.Should().NotContain(UnencryptedMarker);
        xml.Should().NotContain("masterKey");
        xml.Should().Contain("encryptedSecret");
    }

    [Fact]
    public void WithACertificate_APayloadSurvivesARestart()
    {
        // The regression this really guards: ProtectKeysWithCertificate alone makes decryption
        // resolve certificates out of the platform certificate store, which a certificate mounted
        // from a file is not in. That fails only on the *second* startup - the process that wrote
        // the ring still holds it in memory - so a test that protects and unprotects in one
        // provider would pass while every real restart broke every existing session.
        using var certificate = CreateSelfSigned();

        string payload;
        using (var writer = BuildProvider(certificate))
        {
            payload = Protect(writer, "a session cookie");
        }

        using var reader = BuildProvider(certificate);

        Unprotect(reader, payload).Should().Be("a session cookie");
    }

    [Fact]
    public void WithARotatedCertificate_APayloadFromThePreviousOneStillDecrypts()
    {
        // Rotation, as described on DataProtectionConfig.Certificates: the new certificate leads
        // and protects new keys, the outgoing one stays in the list so keys it already protected
        // remain readable until they age out.
        using var outgoing = CreateSelfSigned();
        using var incoming = CreateSelfSigned();

        string payload;
        using (var before = BuildProvider(outgoing))
        {
            payload = Protect(before, "a session cookie");
        }

        using var after = BuildProvider(incoming, outgoing);

        Unprotect(after, payload).Should().Be("a session cookie");
    }

    /// <summary>
    /// Mirrors the Program.cs wiring: the first certificate protects, all of them unprotect.
    /// Passing none leaves the ring unencrypted, exactly as an unconfigured deployment does.
    /// </summary>
    private ServiceProvider BuildProvider(params X509Certificate2?[] certificates)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DataProtectionKeyContext>(options => options.UseSqlite(_connection));

        var dataProtection = services
            .AddDataProtection()
            .SetApplicationName("vfps")
            .PersistKeysToDbContext<DataProtectionKeyContext>();

        var usable = certificates.Where(certificate => certificate is not null).ToArray();
        if (usable.Length > 0)
        {
            dataProtection
                .ProtectKeysWithCertificate(usable[0]!)
                .UnprotectKeysWithAnyCertificate([.. usable!]);
        }

        return services.BuildServiceProvider();
    }

    private static string Protect(IServiceProvider services, string payload) =>
        services
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("vfps.tests")
            .Protect(payload);

    private static string Unprotect(IServiceProvider services, string payload) =>
        services
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("vfps.tests")
            .Unprotect(payload);

    private DataProtectionKeyContext CreateContext() =>
        new(new DbContextOptionsBuilder<DataProtectionKeyContext>().UseSqlite(_connection).Options);

    private string StoredKeyXml()
    {
        using var context = CreateContext();
        var keys = context.DataProtectionKeys.AsNoTracking().ToList();

        keys.Should()
            .ContainSingle("protecting a payload creates exactly one key in an empty ring");

        return keys[0].Xml!;
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
}
