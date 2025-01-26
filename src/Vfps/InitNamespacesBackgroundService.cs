using EntityFramework.Exceptions.Common;
using Vfps.Data;
using Vfps.Protos;
using Vfps.Services;
using NamespaceService = Vfps.Services.NamespaceService;

namespace Vfps;

public class InitNamespacesBackgroundService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<InitNamespacesBackgroundService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var namespaces = configuration
            .GetSection("Init:v1:Namespaces")
            .Get<List<Data.Models.Namespace>>();
        if (namespaces is null || namespaces.Count == 0)
        {
            logger.LogInformation("No namespaces configured to create during startup.");
            return;
        }

        using var scope = serviceProvider.CreateScope();
        var namespaceRepository = scope.ServiceProvider.GetRequiredService<INamespaceRepository>();

        foreach (var @namespace in namespaces)
        {
            logger.LogInformation(
                "Attempting to create namespace {NamespaceName}",
                @namespace.Name
            );

            @namespace.LastUpdatedAt = DateTime.UtcNow;
            @namespace.CreatedAt = DateTime.UtcNow;

            var maybeExistsNamespace = await namespaceRepository.FindAsync(
                @namespace.Name,
                stoppingToken
            );
            if (maybeExistsNamespace is not null)
            {
                logger.LogInformation(
                    "A namespace with the same name {NamespaceName} already exists. Will not be overridden.",
                    @namespace.Name
                );
                return;
            }

            logger.LogInformation(
                "Namespace {NamespaceName} doesn't seem to exist yet, attempting to create.",
                @namespace.Name
            );
            try
            {
                await namespaceRepository.CreateAsync(@namespace, stoppingToken);
                logger.LogInformation(
                    "Successfully created namespace {NamespaceName}.",
                    @namespace.Name
                );
            }
            catch (UniqueConstraintException)
            {
                logger.LogInformation(
                    "A namespace with the same name {NamespaceName} already exists. Will not be overridden.",
                    @namespace.Name
                );
            }
        }
    }
}
