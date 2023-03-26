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
}
