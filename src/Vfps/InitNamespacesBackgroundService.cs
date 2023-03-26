using EntityFramework.Exceptions.Common;
using Vfps.Data;
using Vfps.Protos;
using Vfps.Services;
using NamespaceService = Vfps.Services.NamespaceService;

namespace Vfps;

public class InitNamespacesBackgroundService : BackgroundService
{
    public InitNamespacesBackgroundService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<InitNamespacesBackgroundService> logger
    )
    {
        ServiceProvider = serviceProvider;
        Configuration = configuration;
        Logger = logger;
    }

    private IConfiguration Configuration { get; }
    private ILogger<InitNamespacesBackgroundService> Logger { get; }
    private IServiceProvider ServiceProvider { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var namespaces = Configuration
            .GetSection("Init:v1:Namespaces")
            .Get<List<Data.Models.Namespace>>();
        if (namespaces is null || namespaces.Count == 0)
        {
            Logger.LogInformation("No namespaces configured to create during startup.");
            return;
        }

        using var scope = ServiceProvider.CreateScope();
        var namespaceRepository = scope.ServiceProvider.GetRequiredService<INamespaceRepository>();

        foreach (var @namespace in namespaces)
        {
            Logger.LogInformation(
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
                Logger.LogInformation(
                    "A namespace with the same name {NamespaceName} already exists. Will not be overridden.",
                    @namespace.Name
                );
                return;
            }

            Logger.LogInformation(
                "Namespace {NamespaceName} doesn't seem to exist yet, attempting to create.",
                @namespace.Name
            );
            try
            {
                await namespaceRepository.CreateAsync(@namespace, stoppingToken);
                Logger.LogInformation(
                    "Successfully created namespace {NamespaceName}.",
                    @namespace.Name
                );
            }
            catch (UniqueConstraintException)
            {
                Logger.LogInformation(
                    "A namespace with the same name {NamespaceName} already exists. Will not be overridden.",
                    @namespace.Name
                );
            }
        }
    }
}
