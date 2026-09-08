namespace Vfps.Data;

/// <summary>
/// An abstraction for accessing namespace resources
/// </summary>
public interface INamespaceRepository
{
    /// <summary>
    /// Finds a namespace by its name.
    /// </summary>
    /// <param name="namespaceName">The name of the namespace to get.</param>
    /// <param name="cancellationToken">A cancellation token to abort the action</param>
    /// <returns>The namespace if it exists or null if it doesn't.</returns>
    Task<Models.Namespace?> FindAsync(string namespaceName, CancellationToken cancellationToken);

    /// <summary>
    /// Create a namespace.
    /// </summary>
    /// <param name="namespace">The namespace object</param>
    /// <param name="cancellationToken">A a cancellation token to abort the action</param>
    /// <returns>The created namespace</returns>
    Task<Models.Namespace> CreateAsync(
        Models.Namespace @namespace,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists all namespaces. Namespace cardinality is expected to stay low (unlike pseudonyms),
    /// so this intentionally isn't paginated.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to abort the action</param>
    Task<IReadOnlyList<Models.Namespace>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a namespace and, via the database's own ON DELETE CASCADE foreign key
    /// constraint, every pseudonym it contains. A no-op if the namespace doesn't exist.
    /// </summary>
    /// <param name="namespaceName">The name of the namespace to delete.</param>
    /// <param name="cancellationToken">A cancellation token to abort the action</param>
    Task DeleteAsync(string namespaceName, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the direct children of a namespace - those whose <see cref="Models.Namespace.ParentName"/>
    /// is <paramref name="namespaceName"/>. Non-recursive, and unpaginated for the same
    /// low-cardinality reason as <see cref="GetAllAsync"/>.
    /// </summary>
    /// <param name="namespaceName">The name of the parent namespace.</param>
    /// <param name="cancellationToken">A cancellation token to abort the action</param>
    Task<IReadOnlyList<Models.Namespace>> ListChildrenAsync(
        string namespaceName,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Whether any namespace declares <paramref name="namespaceName"/> as its parent. Used to
    /// refuse deleting a namespace that still has children.
    /// </summary>
    /// <param name="namespaceName">The name of the parent namespace.</param>
    /// <param name="cancellationToken">A cancellation token to abort the action</param>
    Task<bool> HasChildrenAsync(string namespaceName, CancellationToken cancellationToken);
}
