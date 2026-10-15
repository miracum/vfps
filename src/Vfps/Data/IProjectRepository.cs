using Vfps.Data.Models;

namespace Vfps.Data;

/// <summary>
/// An abstraction for accessing projects and their namespace memberships. Project cardinality is
/// expected to stay low, like namespace cardinality, so nothing here is paginated or cached.
/// </summary>
public interface IProjectRepository
{
    /// <summary>
    /// Finds a project by its name, with its <see cref="Project.Memberships"/> loaded.
    /// </summary>
    /// <returns>The project if it exists or null if it doesn't.</returns>
    Task<Project?> FindAsync(string projectName, CancellationToken cancellationToken);

    /// <summary>Lists every project, each with its <see cref="Project.Memberships"/> loaded.</summary>
    Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates a project. Throws <see cref="EntityFramework.Exceptions.Common.UniqueConstraintException"/>
    /// if one with the same name already exists.
    /// </summary>
    Task<Project> CreateAsync(Project project, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a project's description and bumps its
    /// <see cref="TracksCreationAndUpdates.LastUpdatedAt"/>. The description is the only part of a
    /// project that can change - its name is its identity.
    /// </summary>
    /// <returns>Whether a project named <paramref name="projectName"/> existed and was updated.</returns>
    Task<bool> UpdateAsync(
        string projectName,
        string? description,
        DateTimeOffset lastUpdatedAt,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes a project and, via ON DELETE CASCADE, its memberships. The namespaces themselves
    /// are untouched. A no-op if the project doesn't exist.
    /// </summary>
    Task DeleteAsync(string projectName, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a namespace to a project. Idempotent: adding one that is already a member succeeds
    /// without doing anything. Throws
    /// <see cref="EntityFramework.Exceptions.Common.ReferenceConstraintException"/> if the project
    /// or the namespace doesn't exist.
    /// </summary>
    Task AddNamespaceAsync(
        string projectName,
        string namespaceName,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Removes a namespace from a project. A no-op if it isn't a member.
    /// </summary>
    Task RemoveNamespaceAsync(
        string projectName,
        string namespaceName,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Makes <paramref name="projectNames"/> exactly the set of projects a namespace belongs to,
    /// adding and removing memberships as needed - all in one save, so a failure leaves the
    /// previous set in place. Throws
    /// <see cref="EntityFramework.Exceptions.Common.ReferenceConstraintException"/> if the
    /// namespace or any of the projects doesn't exist.
    /// </summary>
    Task SetProjectsOfNamespaceAsync(
        string namespaceName,
        IReadOnlyCollection<string> projectNames,
        CancellationToken cancellationToken
    );
}
