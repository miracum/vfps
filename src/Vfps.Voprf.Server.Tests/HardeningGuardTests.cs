namespace Vfps.Voprf.Server.Tests;

/// <summary>
/// The startup gate that keeps a development configuration from reaching production.
/// </summary>
public class HardeningGuardTests
{
    private static VoprfServerConfig Valid() =>
        new()
        {
            Key = new KeyConfig
            {
                Source = KeySource.Base64,
                Base64 = "AA==",
                KeyId = "v1",
            },
        };

    [Fact]
    public void A_hardened_configuration_is_accepted()
    {
        HardeningGuard.Validate(Valid(), isDevelopment: false).Should().BeEmpty();
    }

    [Fact]
    public void Hardening_may_be_disabled_in_development()
    {
        var config = Valid();
        config.Hardening.IsEnabled = false;

        HardeningGuard.Validate(config, isDevelopment: true).Should().BeEmpty();
    }

    [Fact]
    public void Hardening_may_not_be_disabled_outside_development()
    {
        var config = Valid();
        config.Hardening.IsEnabled = false;

        HardeningGuard
            .Validate(config, isDevelopment: false)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("Hardening:IsEnabled is false outside the Development environment");
    }

    [Fact]
    public void An_ephemeral_key_is_refused_while_hardened()
    {
        var config = Valid();
        config.Key.Source = KeySource.Ephemeral;

        HardeningGuard
            .Validate(config, isDevelopment: true)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("Ephemeral");
    }

    [Fact]
    public void An_ephemeral_key_is_allowed_once_hardening_is_off()
    {
        var config = Valid();
        config.Key.Source = KeySource.Ephemeral;
        config.Hardening.IsEnabled = false;

        HardeningGuard.Validate(config, isDevelopment: true).Should().BeEmpty();
    }

    [Theory]
    [InlineData(KeySource.File)]
    [InlineData(KeySource.Base64)]
    [InlineData(KeySource.Seed)]
    public void A_key_source_with_nowhere_to_read_from_is_refused(KeySource source)
    {
        var config = Valid();
        config.Key.Source = source;
        config.Key.Base64 = string.Empty;
        config.Key.FilePath = string.Empty;

        HardeningGuard.Validate(config, isDevelopment: false).Should().NotBeEmpty();
    }

    [Fact]
    public void Jwt_authentication_without_an_authority_is_refused()
    {
        var config = Valid();
        config.Authentication.Mode = AuthenticationMode.Jwt;

        HardeningGuard
            .Validate(config, isDevelopment: false)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("Jwt:Authority is empty");
    }

    [Fact]
    public void An_empty_key_id_is_refused()
    {
        var config = Valid();
        config.Key.KeyId = "";

        HardeningGuard.Validate(config, isDevelopment: false).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_batch_limit_is_refused(int maxBatchSize)
    {
        var config = Valid();
        config.MaxBatchSize = maxBatchSize;

        HardeningGuard.Validate(config, isDevelopment: false).Should().NotBeEmpty();
    }

    [Fact]
    public void A_rate_limiter_that_permits_nothing_is_refused()
    {
        var config = Valid();
        config.Hardening.RateLimit.PermitsPerWindow = 0;

        HardeningGuard.Validate(config, isDevelopment: false).Should().NotBeEmpty();
    }

    [Fact]
    public void A_disabled_rate_limiter_is_not_validated()
    {
        var config = Valid();
        config.Hardening.RateLimit.IsEnabled = false;
        config.Hardening.RateLimit.PermitsPerWindow = 0;
        config.Hardening.RateLimit.Window = TimeSpan.Zero;

        HardeningGuard.Validate(config, isDevelopment: false).Should().BeEmpty();
    }
}
