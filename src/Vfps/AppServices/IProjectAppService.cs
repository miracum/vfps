using System.Security.Claims;
using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <summary>
/// Project operations shared by the gRPC adapter (<see cref="Services.ProjectService"/>) and
/// Blazor Server components, so business logic and authorization live in exactly one place
/// regardless of caller.
///
/// Projects only group namespaces for display and organization; they grant nothing. Managing them
/// (creating, editing, deleting, changing which namespaces they contain) is admin-only, like
/// managing namespaces themselves. Reading them is not: a caller sees the member namespaces they
/// can read, and a project only if they can read at least one of its namespaces - or are an
/// admin, who sees every project, empty ones included. Everything returned has been filtered that
/// way, so a project's <see cref="Project.Memberships"/> never name a namespace the caller can't
/// read.
/// </summary>
public interface IProjectAppService
{
    /// <summary>
    /// Creates an empty project. <paramref name="projectToCreate"/> only needs Name and
    /// Description populated - CreatedAt/LastUpdatedAt are set here. Requires admin access.
    /// </summary>
    /// <exception cref="ArgumentException">The name is blank.</exception>
    /// <exception cref="ProjectAlreadyExistsException">A project with that name already exists.</exception>
    Task<Project> CreateAsync(
        Project projectToCreate,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>Lists the projects visible to the caller, as described on the interface.</summary>
    Task<IReadOnlyList<Project>> GetAllAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>Gets a single project, as visible to the caller.</summary>
    /// <exception cref="ProjectNotFoundException">No project named <paramref name="projectName"/> exists.</exception>
    /// <exception cref="Authorization.ForbiddenException">
    /// The caller is not an admin and can't read any of the project's namespaces.
    /// </exception>
    Task<Project> GetAsync(
        string projectName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Replaces a project's description, the only part of a project that can change. Null removes
    /// it. Requires admin access.
    /// </summary>
    /// <exception cref="ProjectNotFoundException">No project named <paramref name="projectName"/> exists.</exception>
    Task<Project> UpdateAsync(
        string projectName,
        string? description,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes a project. Its namespaces, and the pseudonyms in them, are not touched - they
    /// simply stop being part of it. Requires admin access.
    /// </summary>
    /// <exception cref="ProjectNotFoundException">No project named <paramref name="projectName"/> exists.</exception>
    Task DeleteAsync(string projectName, ClaimsPrincipal user, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a namespace to a project. Adding one that is already a member succeeds without
    /// changing anything. Requires admin access.
    /// </summary>
    /// <exception cref="ProjectNotFoundException">No project named <paramref name="projectName"/> exists.</exception>
    /// <exception cref="NamespaceNotFoundException">No namespace named <paramref name="namespaceName"/> exists.</exception>
    Task AddNamespaceAsync(
        string projectName,
        string namespaceName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Removes a namespace from a project. Removing one that isn't a member succeeds without
    /// changing anything. Requires admin access.
    /// </summary>
    /// <exception cref="ProjectNotFoundException">No project named <paramref name="projectName"/> exists.</exception>
    /// <exception cref="NamespaceNotFoundException">No namespace named <paramref name="namespaceName"/> exists.</exception>
    Task RemoveNamespaceAsync(
        string projectName,
        string namespaceName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Makes <paramref name="projectNames"/> exactly the set of projects a namespace belongs to,
    /// atomically - the namespace-centric counterpart of the two methods above, for the forms that
    /// edit a namespace's projects as a whole. Requires admin access.
    /// </summary>
    /// <exception cref="NamespaceNotFoundException">No namespace named <paramref name="namespaceName"/> exists.</exception>
    /// <exception cref="ProjectNotFoundException">One of <paramref name="projectNames"/> doesn't exist.</exception>
    Task SetProjectsOfNamespaceAsync(
        string namespaceName,
        IReadOnlyCollection<string> projectNames,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );
}

/// <summary>Thrown when creating a project whose name already exists.</summary>
public class ProjectAlreadyExistsException(string projectName)
    : Exception($"A project named '{projectName}' already exists.")
{
    public string ProjectName { get; } = projectName;
}

public class ProjectNotFoundException(string projectName)
    : Exception($"The requested project '{projectName}' does not exist.")
{
    public string ProjectName { get; } = projectName;
}
