using Vfps.Voprf.Server.Config;

namespace Vfps.Voprf.Server.Security;

/// <summary>
/// Decides whether a configuration is one this server is willing to start with.
/// </summary>
/// <remarks>
/// <para>
/// The protections in <see cref="HardeningConfig"/> can be turned off, because developing
/// against a service that demands TLS and a client certificate is miserable. The risk is that
/// the configuration which made that pleasant follows the service into production, where
/// nothing about a working deployment would reveal that it is answering anyone who asks.
/// </para>
/// <para>
/// So the relaxed configuration is refused outright anywhere but Development. Getting it into
/// production takes deliberately setting the environment as well, which is no longer something
/// that happens by forgetting.
/// </para>
/// <para>
/// A pure function rather than logic inlined into startup so it can be unit-tested against
/// every combination without standing up a host.
/// </para>
/// </remarks>
internal static class HardeningGuard
{
    /// <summary>
    /// Returns the reasons this configuration is unacceptable; empty means it may start.
    /// </summary>
    public static IReadOnlyList<string> Validate(VoprfServerConfig config, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<string>();

        if (!config.Hardening.IsEnabled && !isDevelopment)
        {
            errors.Add(
                $"{VoprfServerConfig.SectionName}:Hardening:IsEnabled is false outside the "
                    + "Development environment. That configuration serves the key over plaintext to "
                    + "unauthenticated callers. Set it to true, or set ASPNETCORE_ENVIRONMENT to "
                    + "Development if this really is a development machine."
            );
        }

        ValidateKey(config.Key, config.Hardening.IsEnabled, errors);

        if (
            config.Hardening.IsEnabled
            && config.Hardening.RequireAuthentication
            && config.Authentication.Mode == AuthenticationMode.Jwt
            && string.IsNullOrWhiteSpace(config.Authentication.Jwt.Authority)
        )
        {
            errors.Add(
                $"{VoprfServerConfig.SectionName}:Authentication:Mode is Jwt but "
                    + "Authentication:Jwt:Authority is empty."
            );
        }

        if (config.MaxBatchSize <= 0)
        {
            errors.Add($"{VoprfServerConfig.SectionName}:MaxBatchSize must be greater than zero.");
        }

        if (config.Hardening.MaxReceiveMessageSizeBytes <= 0)
        {
            errors.Add(
                $"{VoprfServerConfig.SectionName}:Hardening:MaxReceiveMessageSizeBytes must be "
                    + "greater than zero."
            );
        }

        if (config.Hardening.RateLimit.IsEnabled)
        {
            if (config.Hardening.RateLimit.PermitsPerWindow <= 0)
            {
                errors.Add(
                    $"{VoprfServerConfig.SectionName}:Hardening:RateLimit:PermitsPerWindow must be "
                        + "greater than zero while the limiter is enabled."
                );
            }

            if (config.Hardening.RateLimit.Window <= TimeSpan.Zero)
            {
                errors.Add(
                    $"{VoprfServerConfig.SectionName}:Hardening:RateLimit:Window must be greater "
                        + "than zero while the limiter is enabled."
                );
            }
        }

        return errors;
    }

    private static void ValidateKey(KeyConfig key, bool hardened, List<string> errors)
    {
        const string prefix = $"{VoprfServerConfig.SectionName}:Key";

        if (hardened && key.Source == KeySource.Ephemeral)
        {
            errors.Add(
                $"{prefix}:Source is Ephemeral with hardening enabled. An ephemeral key is "
                    + "regenerated on every restart, which silently invalidates every pseudonym "
                    + "already issued. Configure File, Base64, or Seed."
            );
        }

        switch (key.Source)
        {
            case KeySource.File when string.IsNullOrWhiteSpace(key.FilePath):
                errors.Add($"{prefix}:Source is File but {prefix}:FilePath is empty.");
                break;

            case KeySource.Base64 when string.IsNullOrWhiteSpace(key.Base64):
                errors.Add($"{prefix}:Source is Base64 but {prefix}:Base64 is empty.");
                break;

            case KeySource.Seed
                when string.IsNullOrWhiteSpace(key.FilePath)
                    && string.IsNullOrWhiteSpace(key.Base64):
                errors.Add(
                    $"{prefix}:Source is Seed but neither {prefix}:FilePath nor {prefix}:Base64 "
                        + "holds the seed."
                );
                break;

            default:
                break;
        }

        if (string.IsNullOrWhiteSpace(key.KeyId))
        {
            errors.Add(
                $"{prefix}:KeyId is empty. It is returned with every answer so a store of "
                    + "pseudonyms can record which key produced them."
            );
        }
    }
}
