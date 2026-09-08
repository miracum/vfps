using EntityFramework.Exceptions.Common;
using Vfps.Data;

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

        foreach (var @namespace in OrderParentsFirst(namespaces))
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
                continue;
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
            catch (ReferenceConstraintException)
            {
                // The configured parent namespace doesn't exist - neither already in the database
                // nor anywhere in this same Init section. Logged and skipped rather than thrown,
                // matching how this service treats every other per-namespace failure: one bad
                // entry shouldn't stop the rest (or startup itself).
                logger.LogWarning(
                    "Namespace {NamespaceName} declares a parent {ParentName} that does not exist. Skipping.",
                    @namespace.Name,
                    @namespace.ParentName
                );
            }
        }
    }

    /// <summary>
    /// Orders namespaces so a parent is always created before any child that references it,
    /// letting a whole hierarchy be declared in one Init section in any order. Entries whose
    /// parent isn't part of this section keep their original relative order - the parent is then
    /// expected to already exist in the database, and the create fails informatively if it doesn't.
    /// </summary>
    private static List<Data.Models.Namespace> OrderParentsFirst(
        List<Data.Models.Namespace> namespaces
    )
    {
        var byName = new Dictionary<string, Data.Models.Namespace>(StringComparer.Ordinal);
        foreach (var @namespace in namespaces)
        {
            // Last one wins for a duplicated name, rather than throwing the way ToDictionary
            // would - the create below already reports duplicates properly.
            byName[@namespace.Name] = @namespace;
        }

        var ordered = new List<Data.Models.Namespace>(namespaces.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(Data.Models.Namespace @namespace)
        {
            // Doubles as the cycle guard: a config that declares A's parent as B and B's parent
            // as A stops here rather than recursing forever. The resulting order can't satisfy
            // both, so one of them fails its foreign key and is logged and skipped above.
            if (!visited.Add(@namespace.Name))
            {
                return;
            }

            if (
                @namespace.ParentName is not null
                && byName.TryGetValue(@namespace.ParentName, out var parent)
            )
            {
                Visit(parent);
            }

            ordered.Add(@namespace);
        }

        foreach (var @namespace in namespaces)
        {
            Visit(@namespace);
        }

        return ordered;
    }
}
