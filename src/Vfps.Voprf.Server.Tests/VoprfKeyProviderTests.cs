using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vfps.Voprf.Server.Keys;

namespace Vfps.Voprf.Server.Tests;

/// <summary>
/// Loading the keys. Program.cs resolves this singleton during startup rather than leaving it to
/// the first call, so everything that throws here is a process that refuses to start - verified
/// against the real binary, since WebApplicationFactory captures the host at Build and never
/// runs what follows it.
/// </summary>
public class VoprfKeyProviderTests : IDisposable
{
    private readonly List<string> files = [];

    /// <summary>RFC 9497's skSm, base64 encoded.</summary>
    private const string ValidKeyBase64 = "5vc/NEt5s3nxoN034H/2LjjZ9xNFzmKuOpvGCwTM2Qk=";
    private const string ValidPublicKeyHex =
        "c803e2cc6b05fc15064549b5920659ca4a77b2cca6f04f6b357009335476ad4e";

    private static VoprfKeyProvider Create(KeyConfig key) =>
        new(
            Options.Create(new VoprfServerConfig { Key = key }),
            NullLogger<VoprfKeyProvider>.Instance
        );

    private static byte[] PublicKeyOf(VoprfKeyProvider provider) =>
        provider.KeyPair.PublicKey.ToArray();

    private string WriteFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllBytes(path, content);
        files.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in files.Where(File.Exists))
        {
            File.Delete(path);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_base64_key_loads_and_yields_the_matching_public_key()
    {
        using var provider = Create(
            new KeyConfig
            {
                Source = KeySource.Base64,
                Base64 = ValidKeyBase64,
                KeyId = "v1",
            }
        );

        Convert.ToHexStringLower(provider.KeyPair.PublicKey).Should().Be(ValidPublicKeyHex);
        provider.KeyId.Should().Be("v1");
    }

    [Fact]
    public void A_key_file_of_raw_bytes_loads()
    {
        var path = WriteFile(Convert.FromBase64String(ValidKeyBase64));

        using var provider = Create(new KeyConfig { Source = KeySource.File, FilePath = path });

        Convert.ToHexStringLower(PublicKeyOf(provider)).Should().Be(ValidPublicKeyHex);
    }

    [Fact]
    public void A_key_file_of_base64_text_loads()
    {
        // What `openssl rand -base64 32` and most secret tooling produce, trailing newline
        // included.
        var path = WriteFile(System.Text.Encoding.UTF8.GetBytes(ValidKeyBase64 + "\n"));

        using var provider = Create(new KeyConfig { Source = KeySource.File, FilePath = path });

        Convert.ToHexStringLower(PublicKeyOf(provider)).Should().Be(ValidPublicKeyHex);
    }

    [Fact]
    public void A_value_above_the_group_order_is_refused()
    {
        // The reason `openssl rand 32 > voprf.key` is the wrong instruction: the group order is
        // just above 2^252, so most random 32-byte values land here. Seeds have no such limit.
        var tooLarge = Convert.ToBase64String([.. Enumerable.Repeat((byte)0xff, 32)]);

        var load = () => Create(new KeyConfig { Source = KeySource.Base64, Base64 = tooLarge });

        load.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_missing_key_file_is_refused()
    {
        var load = () =>
            Create(
                new KeyConfig
                {
                    Source = KeySource.File,
                    FilePath = Path.Combine(Path.GetTempPath(), "does-not-exist.key"),
                }
            );

        load.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_key_file_of_the_wrong_shape_is_refused()
    {
        var path = WriteFile(System.Text.Encoding.UTF8.GetBytes("not-a-key"));

        var load = () => Create(new KeyConfig { Source = KeySource.File, FilePath = path });

        load.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_seed_derives_the_same_key_every_time_and_key_info_separates_them()
    {
        // Exactly what `openssl rand 32` produces. Unlike a key, arbitrary random bytes are a
        // valid seed - DeriveKeyPair is what turns them into a scalar below the group order.
        var seed = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)
        );

        using var first = Create(
            new KeyConfig
            {
                Source = KeySource.Seed,
                Base64 = seed,
                KeyInfo = "column-a",
            }
        );
        using var second = Create(
            new KeyConfig
            {
                Source = KeySource.Seed,
                Base64 = seed,
                KeyInfo = "column-a",
            }
        );
        using var other = Create(
            new KeyConfig
            {
                Source = KeySource.Seed,
                Base64 = seed,
                KeyInfo = "column-b",
            }
        );

        PublicKeyOf(first).Should().Equal(PublicKeyOf(second));
        PublicKeyOf(first).Should().NotEqual(PublicKeyOf(other));
    }

    [Fact]
    public void An_ephemeral_key_differs_on_every_load()
    {
        using var first = Create(new KeyConfig { Source = KeySource.Ephemeral });
        using var second = Create(new KeyConfig { Source = KeySource.Ephemeral });

        PublicKeyOf(first).Should().NotEqual(PublicKeyOf(second));
    }
}
