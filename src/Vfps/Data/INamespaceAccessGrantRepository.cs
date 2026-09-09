using Vfps.Data.Models;

namespace Vfps.Data;

/// <summary>
/// Store and retrieve the namespace access rules that replaced the old, static
/// <c>Authorization:NamespaceRules</c> configuration section.
/// </summary>
public interface INamespaceAccessGrantRepository
{
    /// <summary>
    /// Every grant, namespace-scoped and global alike. Unpaginated for the same low-cardinality
    /// reason as <see cref="INamespaceRepository.GetAllAsync"/> - and because
    /// <see cref="Authorization.INamespacePermissionChecker"/> resolves a caller's permissions
    /// against the whole set at once rather than querying per namespace.
    /// </summary>
    Task<IReadOnlyList<NamespaceAccessGrant>> GetAllAsync(CancellationToken cancellationToken);

    Task<NamespaceAccessGrant?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The grant for this exact (namespace, grantee) combination, if one exists.
    /// <paramref name="namespaceName"/> is null for a grant covering every namespace.
    /// </summary>
    Task<NamespaceAccessGrant?> FindByGranteeAsync(
        string? namespaceName,
        GranteeType granteeType,
        string grantee,
        CancellationToken cancellationToken
    );

    Task<NamespaceAccessGrant> CreateAsync(
        NamespaceAccessGrant grant,
        CancellationToken cancellationToken
    );

    Task UpdateAsync(NamespaceAccessGrant grant, CancellationToken cancellationToken);

    /// <summary>Deletes a grant by id. A no-op if it doesn't exist.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
