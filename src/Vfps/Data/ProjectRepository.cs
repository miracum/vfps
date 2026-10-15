using EntityFramework.Exceptions.Common;
using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;

namespace Vfps.Data;

public class ProjectRepository(IDbContextFactory<PseudonymContext> contextFactory)
    : IProjectRepository
{
    /// <inheritdoc/>
    public async Task<Project?> FindAsync(string projectName, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .Projects.AsNoTracking()
            .Include(p => p.Memberships)
            .FirstOrDefaultAsync(p => p.Name == projectName, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .Projects.AsNoTracking()
            .Include(p => p.Memberships)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Project> CreateAsync(Project project, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Add(project);
        await context.SaveChangesAsync(cancellationToken);
        return project;
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateAsync(
        string projectName,
        string? description,
        DateTimeOffset lastUpdatedAt,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var updated = await context
            .Projects.Where(p => p.Name == projectName)
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(p => p.Description, description)
                        .SetProperty(p => p.LastUpdatedAt, lastUpdatedAt),
                cancellationToken
            );
        return updated > 0;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string projectName, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context
            .Projects.Where(p => p.Name == projectName)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task AddNamespaceAsync(
        string projectName,
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (
            await context.ProjectNamespaces.AnyAsync(
                m => m.ProjectName == projectName && m.NamespaceName == namespaceName,
                cancellationToken
            )
        )
        {
            return;
        }

        context.Add(
            new ProjectNamespace { ProjectName = projectName, NamespaceName = namespaceName }
        );
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintException)
        {
            // Added by a concurrent caller between the check and the save - which is the outcome
            // that was asked for.
        }
    }

    /// <inheritdoc/>
    public async Task RemoveNamespaceAsync(
        string projectName,
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context
            .ProjectNamespaces.Where(m =>
                m.ProjectName == projectName && m.NamespaceName == namespaceName
            )
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task SetProjectsOfNamespaceAsync(
        string namespaceName,
        IReadOnlyCollection<string> projectNames,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var wanted = projectNames.ToHashSet(StringComparer.Ordinal);
        var existing = await context
            .ProjectNamespaces.AsNoTracking()
            .Where(m => m.NamespaceName == namespaceName)
            .ToListAsync(cancellationToken);

        // One SaveChanges is one transaction, which is why this isn't a pair of bulk statements:
        // the deployed provider retries on transient failures, and an explicit transaction would
        // then have to be wrapped in the execution strategy as well.
        context.RemoveRange(existing.Where(m => !wanted.Contains(m.ProjectName)));
        context.AddRange(
            wanted
                .Except(existing.Select(m => m.ProjectName), StringComparer.Ordinal)
                .Select(projectName => new ProjectNamespace
                {
                    ProjectName = projectName,
                    NamespaceName = namespaceName,
                })
        );

        await context.SaveChangesAsync(cancellationToken);
    }
}
