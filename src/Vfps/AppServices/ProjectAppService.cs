using System.Security.Claims;
using EntityFramework.Exceptions.Common;
using Vfps.Authorization;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <inheritdoc cref="IProjectAppService"/>
public class ProjectAppService(
    IProjectRepository projectRepository,
    INamespaceRepository namespaceRepository,
    INamespacePermissionChecker permissionChecker
) : IProjectAppService
{
    /// <inheritdoc/>
    public async Task<Project> CreateAsync(
        Project projectToCreate,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        RequireAdmin(user, "Creating a project");

        if (string.IsNullOrWhiteSpace(projectToCreate.Name))
        {
            throw new ArgumentException("A project name is required.", nameof(projectToCreate));
        }

        var now = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Name = projectToCreate.Name,
            Description = projectToCreate.Description,
            CreatedAt = now,
            LastUpdatedAt = now,
        };

        try
        {
            await projectRepository.CreateAsync(project, cancellationToken);
        }
        catch (UniqueConstraintException)
        {
            throw new ProjectAlreadyExistsException(project.Name);
        }

        return project;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Project>> GetAllAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var projects = await projectRepository.GetAllAsync(cancellationToken);
        var permissions = await permissionChecker.ResolveAsync(user, cancellationToken);

        return [.. projects.Where(p => TryRestrictToVisible(p, permissions))];
    }

    /// <inheritdoc/>
    public async Task<Project> GetAsync(
        string projectName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var project =
            await projectRepository.FindAsync(projectName, cancellationToken)
            ?? throw new ProjectNotFoundException(projectName);

        var permissions = await permissionChecker.ResolveAsync(user, cancellationToken);
        if (!TryRestrictToVisible(project, permissions))
        {
            throw new ForbiddenException(
                $"Read access to at least one namespace of project '{projectName}' is required."
            );
        }

        return project;
    }

    /// <inheritdoc/>
    public async Task<Project> UpdateAsync(
        string projectName,
        string? description,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        RequireAdmin(user, "Editing a project");

        if (
            !await projectRepository.UpdateAsync(
                projectName,
                description,
                DateTimeOffset.UtcNow,
                cancellationToken
            )
        )
        {
            throw new ProjectNotFoundException(projectName);
        }

        return await projectRepository.FindAsync(projectName, cancellationToken)
            ?? throw new ProjectNotFoundException(projectName);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(
        string projectName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        RequireAdmin(user, "Deleting a project");

        if (await projectRepository.FindAsync(projectName, cancellationToken) is null)
        {
            throw new ProjectNotFoundException(projectName);
        }

        await projectRepository.DeleteAsync(projectName, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task AddNamespaceAsync(
        string projectName,
        string namespaceName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        RequireAdmin(user, "Changing the namespaces of a project");
        await EnsureProjectAndNamespaceExistAsync(projectName, namespaceName, cancellationToken);

        try
        {
            await projectRepository.AddNamespaceAsync(
                projectName,
                namespaceName,
                cancellationToken
            );
        }
        catch (ReferenceConstraintException)
        {
            // Deleted between the checks above and the insert. Which of the two it was isn't
            // known without asking again, so ask.
            await EnsureProjectAndNamespaceExistAsync(
                projectName,
                namespaceName,
                cancellationToken
            );
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task RemoveNamespaceAsync(
        string projectName,
        string namespaceName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        RequireAdmin(user, "Changing the namespaces of a project");
        await EnsureProjectAndNamespaceExistAsync(projectName, namespaceName, cancellationToken);

        await projectRepository.RemoveNamespaceAsync(projectName, namespaceName, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task SetProjectsOfNamespaceAsync(
        string namespaceName,
        IReadOnlyCollection<string> projectNames,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        RequireAdmin(user, "Changing the projects of a namespace");

        if (await namespaceRepository.FindAsync(namespaceName, cancellationToken) is null)
        {
            throw new NamespaceNotFoundException(namespaceName);
        }

        var existing = (await projectRepository.GetAllAsync(cancellationToken))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        var distinct = projectNames.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.FirstOrDefault(name => !existing.Contains(name)) is { } missing)
        {
            throw new ProjectNotFoundException(missing);
        }

        try
        {
            await projectRepository.SetProjectsOfNamespaceAsync(
                namespaceName,
                distinct,
                cancellationToken
            );
        }
        catch (ReferenceConstraintException)
        {
            // Something was deleted after the checks above; report the one that is gone.
            if (await namespaceRepository.FindAsync(namespaceName, cancellationToken) is null)
            {
                throw new NamespaceNotFoundException(namespaceName);
            }

            throw;
        }
    }

    private void RequireAdmin(ClaimsPrincipal user, string action)
    {
        // Gated like namespace creation, editing and deletion, and for the same reason: a project
        // is not scoped to a namespace the caller could hold a write grant for.
        if (!permissionChecker.IsAdmin(user))
        {
            throw new ForbiddenException($"{action} requires admin access.");
        }
    }

    private async Task EnsureProjectAndNamespaceExistAsync(
        string projectName,
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        if (await projectRepository.FindAsync(projectName, cancellationToken) is null)
        {
            throw new ProjectNotFoundException(projectName);
        }

        if (await namespaceRepository.FindAsync(namespaceName, cancellationToken) is null)
        {
            throw new NamespaceNotFoundException(namespaceName);
        }
    }

    /// <summary>
    /// Narrows a freshly loaded project - never a shared or cached one, since this mutates it - to
    /// the namespaces the caller can read, and says whether the caller may see it at all.
    /// </summary>
    private static bool TryRestrictToVisible(Project project, NamespacePermissions permissions)
    {
        if (permissions.IsAdmin)
        {
            return true;
        }

        project.Memberships =
        [
            .. project.Memberships.Where(m => permissions.HasReadAccess(m.NamespaceName)),
        ];

        return project.Memberships.Count > 0;
    }
}
