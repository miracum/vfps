using Vfps.Voprf.Client;

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
    [Fact]
    public async Task A_value_becomes_the_pseudonym_the_key_holder_would_have_computed()
    {
        using var factory = new VoprfTestServer();
        var pseudonymizer = factory.CreatePseudonymizer();

        var pseudonym = await pseudonymizer.PseudonymizeAsync(
            "alice@example.com",
            TestContext.Current.CancellationToken
        );

        pseudonym.KeyId.Should().Be("test-key");

        // Qualified with the generation that produced it, so the stored value alone says which
        // key it belongs to - see VoprfClientOptions.IncludeKeyIdInPseudonym.
        pseudonym
            .Value.Should()
            .Be(
                "test-key." + Base64Url.EncodeToString(VoprfTestServer.Locally("alice@example.com"))
            );
    }

    [Fact]
    public async Task The_same_value_always_gives_the_same_pseudonym()
    {
        using var factory = new VoprfTestServer();
        var pseudonymizer = factory.CreatePseudonymizer();

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
        using var factory = new VoprfTestServer();
        var pseudonymizer = factory.CreatePseudonymizer();

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
        using var factory = new VoprfTestServer();
        var pseudonymizer = factory.CreatePseudonymizer();

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
        using var factory = new VoprfTestServer();
        var pseudonymizer = factory.CreatePseudonymizer();

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
        using var factory = new VoprfTestServer();
        var pseudonymizer = factory.CreatePseudonymizer(options => options.Normalization = null);

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
        using var factory = new VoprfTestServer();
        var pseudonymizer = factory.CreatePseudonymizer(options =>
        {
            options.Format = PseudonymFormat.Hex;
            options.Length = 16;
        });

        var pseudonym = await pseudonymizer.PseudonymizeAsync(
            "alice@example.com",
            TestContext.Current.CancellationToken
        );

        // The key id prefix is not part of the truncated pseudonym - Length counts the output.
        const string Qualifier = "test-key.";
        pseudonym.Value.Should().StartWith(Qualifier);

        var truncated = pseudonym.Value[Qualifier.Length..];
        truncated.Should().HaveLength(32);
        Convert
            .ToHexStringLower(VoprfTestServer.Locally("alice@example.com"))
            .Should()
            .StartWith(truncated);
    }

    [Fact]
    public async Task The_key_id_can_be_left_out_of_the_pseudonym()
    {
        // For a deployment whose stored values have to match what another RFC 9497 implementation
        // computes from the same key, byte for byte.
        using var factory = new VoprfTestServer();
        var pseudonymizer = factory.CreatePseudonymizer(options =>
            options.IncludeKeyIdInPseudonym = false
        );

        var pseudonym = await pseudonymizer.PseudonymizeAsync(
            "alice@example.com",
            TestContext.Current.CancellationToken
        );

        pseudonym
            .Value.Should()
            .Be(Base64Url.EncodeToString(VoprfTestServer.Locally("alice@example.com")));
        pseudonym.KeyId.Should().Be("test-key");
    }

    [Fact]
    public async Task A_qualified_pseudonym_splits_back_into_its_key_id_and_pseudonym()
    {
        // The property the prefix exists for: a stored value on its own says which key produced
        // it, which is what makes a rotation migratable row by row.
        using var factory = new VoprfTestServer();
        var pseudonymizer = factory.CreatePseudonymizer();

        var pseudonym = await pseudonymizer.PseudonymizeAsync(
            "alice@example.com",
            TestContext.Current.CancellationToken
        );

        var separator = pseudonym.Value.IndexOf('.', StringComparison.Ordinal);
        separator.Should().BePositive();

        pseudonym.Value[..separator].Should().Be(pseudonym.KeyId);
        pseudonym
            .Value[(separator + 1)..]
            .Should()
            .Be(Base64Url.EncodeToString(VoprfTestServer.Locally("alice@example.com")));
    }

    [Fact]
    public async Task An_empty_value_is_rejected_rather_than_given_a_stable_pseudonym()
    {
        using var factory = new VoprfTestServer();
        var pseudonymizer = factory.CreatePseudonymizer();

        var pseudonymize = async () =>
            await pseudonymizer.PseudonymizeAsync("", TestContext.Current.CancellationToken);

        await pseudonymize.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_server_evaluating_under_a_different_key_than_the_pin_is_caught()
    {
        using var factory = new VoprfTestServer();

        // The attack the verifiable variant exists for, from the client's side: whatever the
        // server actually holds, an answer that does not match the pinned key is refused.
        using var other = VoprfKeyPair.Generate();
        var pseudonymizer = factory.CreatePseudonymizer(options =>
            options.PublicKey = Convert.ToHexStringLower(other.PublicKey)
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
        using var factory = new VoprfTestServer();
        var pseudonymizer = factory.CreatePseudonymizer(options =>
            options.ExpectedKeyId = "some-other-key"
        );

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
            PublicKey = VoprfTestServer.PublicKeyHex,
            Length = length,
        };

        var validate = options.Validate;

        validate.Should().Throw<VoprfClientConfigurationException>();
    }
}
