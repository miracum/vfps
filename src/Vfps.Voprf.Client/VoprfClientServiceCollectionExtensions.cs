using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vfps.Voprf.Protos;

namespace Vfps.Voprf.Client;

/// <summary>
/// Registers <see cref="IVoprfPseudonymizer"/> and the gRPC channel behind it.
/// </summary>
public static class VoprfClientServiceCollectionExtensions
{
    /// <summary>
    /// Adds a pseudonymizer talking to the server described by the <c>Voprf</c> configuration
    /// section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// Configuration root or the <c>Voprf</c> section itself.
    /// </param>
    public static IServiceCollection AddVoprfPseudonymizer(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(VoprfClientOptions.SectionName).Exists()
            ? configuration.GetSection(VoprfClientOptions.SectionName)
            : configuration;

        var options = new VoprfClientOptions();
        section.Bind(options);

        return services.AddVoprfPseudonymizer(configured =>
        {
            configured.Address = options.Address;
            configured.PublicKey = options.PublicKey;
            configured.ExpectedKeyId = options.ExpectedKeyId;
            configured.AllowUnpinnedPublicKey = options.AllowUnpinnedPublicKey;
            configured.Format = options.Format;
            configured.Length = options.Length;
            configured.Normalization = options.Normalization;
        });
    }

    /// <summary>Adds a pseudonymizer configured in code.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options.</param>
    public static IServiceCollection AddVoprfPseudonymizer(
        this IServiceCollection services,
        Action<VoprfClientOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        var options = new VoprfClientOptions();
        configure(options);

        // Validated here rather than on first use: a missing pinned public key or an unreachable
        // address is a deployment mistake, and finding out at startup beats finding out when the
        // first record needs pseudonymizing.
        options.Validate();

        services.AddGrpcClient<VoprfService.VoprfServiceClient>(grpc =>
            grpc.Address = new Uri(options.Address)
        );

        services.AddSingleton<IVoprfPseudonymizer, VoprfPseudonymizer>();

        return services;
    }
}
