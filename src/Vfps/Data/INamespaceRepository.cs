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
    /// <returns>The namespace if it exists or null if it doesn't.</returns>
    Task<Models.Namespace?> FindAsync(string namespaceName);
}
