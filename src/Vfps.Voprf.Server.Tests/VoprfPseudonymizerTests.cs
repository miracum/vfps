using System.Diagnostics.CodeAnalysis;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vfps.Voprf.Client;
using Vfps.Voprf.Protos;

namespace Vfps.Voprf.Server.Tests;

/// <summary>
/// The client against a real server: a value goes in, a pseudonym comes out, and the server
/// never sees the value.
/// </summary>
/// <remarks>
/// These live in the server's test project rather than a project of their own because they need
/// a running server, and the host factory here already solves the awkward part of that - see the
/// remarks on <see cref="VoprfServerEndToEndTests"/>'s factory for why configuration has to
/// arrive as environment variables.
/// </remarks>
public class VoprfPseudonymizerTests
{
    /// <summary>RFC 9497's skSm, so expected outputs can be computed locally.</summary>
    private const string PrivateKeyBase64 = "5vc/NEt5s3nxoN034H/2LjjZ9xNFzmKuOpvGCwTM2Qk=";
    private const string PublicKeyHex =
        "c803e2cc6b05fc15064549b5920659ca4a77b2cca6f04f6b357009335476ad4e";

    [ExcludeFromCodeCoverage]
    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly List<string> variables = [];

        public Factory()
        {
            var settings = new Dictionary<string, string?>
            {
                ["VoprfServer:Key:Source"] = "Base64",
                ["VoprfServer:Key:Base64"] = PrivateKeyBase64,
                ["VoprfServer:Key:KeyId"] = "test-key",
                ["VoprfServer:Hardening:IsEnabled"] = "false",
            };

            foreach (var (key, value) in settings)
            {
                var name = key.Replace(":", "__", StringComparison.Ordinal);
                Environment.SetEnvironmentVariable(name, value);
                variables.Add(name);
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Development");

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            foreach (var name in variables)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    private static IVoprfPseudonymizer Create(
        Factory factory,
        Action<VoprfClientOptions>? configure = null
    )
    {
        var channel = GrpcChannel.ForAddress(
            factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() }
        );

        var options = new VoprfClientOptions
        {
            Address = factory.Server.BaseAddress.ToString(),
            PublicKey = PublicKeyHex,
        };
        configure?.Invoke(options);

        return new VoprfPseudonymizer(
            new VoprfService.VoprfServiceClient(channel),
            Options.Create(options),
            NullLogger<VoprfPseudonymizer>.Instance
        );
    }

    /// <summary>What the key holder would compute for the same value, for comparison.</summary>
    private static byte[] Locally(string value)
    {
        using var keyPair = VoprfKeyPair.Import(Convert.FromBase64String(PrivateKeyBase64));
        return VoprfServer.Evaluate(keyPair, System.Text.Encoding.UTF8.GetBytes(value));
    }

    [Fact]
    public async Task A_value_becomes_the_pseudonym_the_key_holder_would_have_computed()
    {
        using var factory = new Factory();
        var pseudonymizer = Create(factory);

        var pseudonym = await pseudonymizer.PseudonymizeAsync(
            "alice@example.com",
            TestContext.Current.CancellationToken
        );

        pseudonym.KeyId.Should().Be("test-key");
        pseudonym.Value.Should().Be(Base64Url.EncodeToString(Locally("alice@example.com")));
    }

    [Fact]
    public async Task The_same_value_always_gives_the_same_pseudonym()
    {
        using var factory = new Factory();
        var pseudonymizer = Create(factory);

        var first = await pseudonymizer.PseudonymizeAsync(
            "alice@example.com",
            TestContext.Current.CancellationToken
        );
        var second = await pseudonymizer.PseudonymizeAsync(
            "alice@example.com",
            TestContext.Current.CancellationToken
        );

        // Each call blinds with a fresh scalar, so the server saw two unrelated elements.
        second.Value.Should().Be(first.Value);
    }

    [Fact]
    public async Task Different_values_give_different_pseudonyms()
    {
        using var factory = new Factory();
        var pseudonymizer = Create(factory);

        var alice = await pseudonymizer.PseudonymizeAsync(
            "alice@example.com",
            TestContext.Current.CancellationToken
        );
        var bob = await pseudonymizer.PseudonymizeAsync(
            "bob@example.com",
            TestContext.Current.CancellationToken
        );

        bob.Value.Should().NotBe(alice.Value);
    }

    [Fact]
    public async Task A_batch_agrees_with_the_same_values_sent_one_at_a_time()
    {
        using var factory = new Factory();
        var pseudonymizer = Create(factory);

        string[] values = ["alice@example.com", "bob@example.com", "carol@example.com"];

        var batched = await pseudonymizer.PseudonymizeAsync(
            values,
            TestContext.Current.CancellationToken
        );

        batched.Should().HaveCount(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var single = await pseudonymizer.PseudonymizeAsync(
                values[i],
                TestContext.Current.CancellationToken
            );
            batched[i].Value.Should().Be(single.Value);
        }
    }

    [Fact]
    public async Task Composed_and_decomposed_unicode_agree_under_the_default_normalization()
    {
        using var factory = new Factory();
        var pseudonymizer = Create(factory);

        // "cafe" with a precomposed e-acute, and with e + a combining acute accent. They render identically,
        // arrive from different platforms, and are different byte strings.
        const string Composed = "caf\u00e9@example.com";
        const string Decomposed = "cafe\u0301@example.com";
        // Written as escapes on purpose: the two differ only in bytes, and a file-wide
        // Unicode normalisation would otherwise quietly turn this test into a tautology.
        Composed.Should().NotBe(Decomposed);

        var composed = await pseudonymizer.PseudonymizeAsync(
            Composed,
            TestContext.Current.CancellationToken
        );
        var decomposed = await pseudonymizer.PseudonymizeAsync(
            Decomposed,
            TestContext.Current.CancellationToken
        );

        decomposed.Value.Should().Be(composed.Value);
    }

    [Fact]
    public async Task Without_normalization_the_two_forms_file_the_same_person_twice()
    {
        using var factory = new Factory();
        var pseudonymizer = Create(factory, options => options.Normalization = null);

        var composed = await pseudonymizer.PseudonymizeAsync(
            "caf\u00e9@example.com",
            TestContext.Current.CancellationToken
        );
        var decomposed = await pseudonymizer.PseudonymizeAsync(
            "cafe\u0301@example.com",
            TestContext.Current.CancellationToken
        );

        decomposed.Value.Should().NotBe(composed.Value);
    }

    [Fact]
    public async Task A_truncated_hex_pseudonym_is_a_prefix_of_the_full_output()
    {
        using var factory = new Factory();
        var pseudonymizer = Create(
            factory,
            options =>
            {
                options.Format = PseudonymFormat.Hex;
                options.Length = 16;
            }
        );

        var pseudonym = await pseudonymizer.PseudonymizeAsync(
            "alice@example.com",
            TestContext.Current.CancellationToken
        );

        pseudonym.Value.Should().HaveLength(32);
        Convert.ToHexStringLower(Locally("alice@example.com")).Should().StartWith(pseudonym.Value);
    }

    [Fact]
    public async Task An_empty_value_is_rejected_rather_than_given_a_stable_pseudonym()
    {
        using var factory = new Factory();
        var pseudonymizer = Create(factory);

        var pseudonymize = async () =>
            await pseudonymizer.PseudonymizeAsync("", TestContext.Current.CancellationToken);

        await pseudonymize.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_server_evaluating_under_a_different_key_than_the_pin_is_caught()
    {
        using var factory = new Factory();

        // The attack the verifiable variant exists for, from the client's side: whatever the
        // server actually holds, an answer that does not match the pinned key is refused.
        using var other = VoprfKeyPair.Generate();
        var pseudonymizer = Create(
            factory,
            options => options.PublicKey = Convert.ToHexStringLower(other.PublicKey)
        );

        var pseudonymize = async () =>
            await pseudonymizer.PseudonymizeAsync(
                "alice@example.com",
                TestContext.Current.CancellationToken
            );

        await pseudonymize.Should().ThrowAsync<VoprfVerificationException>();
    }

    [Fact]
    public async Task An_answer_from_an_unexpected_generation_is_refused()
    {
        using var factory = new Factory();
        var pseudonymizer = Create(factory, options => options.ExpectedKeyId = "some-other-key");

        var pseudonymize = async () =>
            await pseudonymizer.PseudonymizeAsync(
                "alice@example.com",
                TestContext.Current.CancellationToken
            );

        (await pseudonymize.Should().ThrowAsync<VoprfException>())
            .Which.Message.Should()
            .Contain("some-other-key");
    }

    [Fact]
    public void A_client_with_no_pinned_public_key_is_refused()
    {
        var options = new VoprfClientOptions { Address = "https://voprf:8081" };

        var validate = options.Validate;

        validate
            .Should()
            .Throw<VoprfClientConfigurationException>()
            .Which.Message.Should()
            .Contain("PublicKey is empty");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(65)]
    public void A_pseudonym_length_outside_the_supported_range_is_refused(int length)
    {
        var options = new VoprfClientOptions
        {
            Address = "https://voprf:8081",
            PublicKey = PublicKeyHex,
            Length = length,
        };

        var validate = options.Validate;

        validate.Should().Throw<VoprfClientConfigurationException>();
    }
}
