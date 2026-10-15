using EntityFramework.Exceptions.Common;
using Vfps.Data;

namespace Vfps;

public class InitNamespacesBackgroundService(
    INamespaceRepository namespaceRepository,
    IProjectRepository projectRepository,
    IConfiguration configuration,
    ILogger<InitNamespacesBackgroundService> logger
) : BackgroundService
{
    /// <summary>
    /// One entry of the <c>Init:v1:Projects</c> section: a project to create, and the namespaces to
    /// put into it. Declared on the project's side, rather than as a property of each namespace,
    /// since a namespace can be in any number of projects.
    /// </summary>
    public class InitProject
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public List<string> Namespaces { get; set; } = [];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InitNamespacesAsync(stoppingToken);

        // After the namespaces, so a project can name one that is created in the very same
        // section.
        await InitProjectsAsync(stoppingToken);
    }

    private async Task InitNamespacesAsync(CancellationToken stoppingToken)
    {
        var namespaces = configuration
            .GetSection("Init:v1:Namespaces")
            .Get<List<Data.Models.Namespace>>();
        if (namespaces is null || namespaces.Count == 0)
        {
            logger.LogInformation("No namespaces configured to create during startup.");
            return;
        }

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

    private async Task InitProjectsAsync(CancellationToken stoppingToken)
    {
        var projects = configuration.GetSection("Init:v1:Projects").Get<List<InitProject>>();
        if (projects is null || projects.Count == 0)
        {
            logger.LogInformation("No projects configured to create during startup.");
            return;
        }

        foreach (var project in projects)
        {
            if (string.IsNullOrWhiteSpace(project.Name))
            {
                logger.LogWarning("Skipping a configured project without a name.");
                continue;
            }

            if (await projectRepository.FindAsync(project.Name, stoppingToken) is null)
            {
                logger.LogInformation("Attempting to create project {ProjectName}", project.Name);

                var now = DateTimeOffset.UtcNow;
                try
                {
                    await projectRepository.CreateAsync(
                        new Data.Models.Project
                        {
                            Name = project.Name,
                            Description = project.Description,
                            CreatedAt = now,
                            LastUpdatedAt = now,
                        },
                        stoppingToken
                    );
                }
                catch (UniqueConstraintException)
                {
                    // Created by another replica starting up at the same time.
                }
            }
            else
            {
                // Like a namespace, an existing project is not overridden: its description may
                // have been edited since. Its configured namespaces are still added below - that
                // is idempotent, and lets a new namespace be put into an existing project.
                logger.LogInformation(
                    "A project with the same name {ProjectName} already exists. Will not be overridden.",
                    project.Name
                );
            }

            foreach (var namespaceName in project.Namespaces)
            {
                try
                {
                    await projectRepository.AddNamespaceAsync(
                        project.Name,
                        namespaceName,
                        stoppingToken
                    );
                }
                catch (ReferenceConstraintException)
                {
                    // Logged and skipped like a missing parent above: one bad entry shouldn't
                    // stop the rest, or startup itself.
                    logger.LogWarning(
                        "Project {ProjectName} lists a namespace {NamespaceName} that does not exist. Skipping.",
                        project.Name,
                        namespaceName
                    );
                }
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
