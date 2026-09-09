using System.Security.Claims;
using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <summary>
/// Managing the namespace access rules that used to live in the static
/// <c>Authorization:NamespaceRules</c> configuration section. Every method requires admin access
/// (<see cref="Config.AuthorizationConfig.AdminRoles"/>) - being able to grant yourself access to
/// a namespace is exactly the same authority as having it.
///
/// Shaped like <see cref="INamespaceAppService"/>: the caller's
/// <see cref="ClaimsPrincipal"/> is passed explicitly rather than resolved ambiently, so it works
/// identically from a Blazor circuit and from any future API surface.
/// </summary>
public interface INamespaceAccessGrantAppService
{
    /// <summary>Every grant, namespace-scoped and global alike.</summary>
    Task<IReadOnlyList<NamespaceAccessGrant>> GetAllAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates a grant. <paramref name="namespaceName"/> is null for one that applies to every
    /// namespace. <paramref name="grantee"/> is a role name or an email address, according to
    /// <paramref name="granteeType"/>; it's trimmed, and lower-cased for
    /// <see cref="GranteeType.Email"/>, before being stored.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="grantee"/> is blank, isn't a valid email address for an
    /// <see cref="GranteeType.Email"/> grant, or no permission at all was granted.
    /// </exception>
    /// <exception cref="NamespaceNotFoundException">
    /// <paramref name="namespaceName"/> is set but no such namespace exists.
    /// </exception>
    /// <exception cref="NamespaceAccessGrantAlreadyExistsException">
    /// This grantee already has a grant for this namespace - edit that one instead.
    /// </exception>
    Task<NamespaceAccessGrant> CreateAsync(
        string? namespaceName,
        GranteeType granteeType,
        string grantee,
        bool canRead,
        bool canWrite,
        bool canReverseLookup,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Replaces an existing grant's three permission flags. The namespace and grantee aren't
    /// editable - change either and it's a different grant.
    /// </summary>
    /// <exception cref="NamespaceAccessGrantNotFoundException">No grant with this id exists.</exception>
    Task<NamespaceAccessGrant> UpdateAsync(
        Guid id,
        bool canRead,
        bool canWrite,
        bool canReverseLookup,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <exception cref="NamespaceAccessGrantNotFoundException">No grant with this id exists.</exception>
    Task DeleteAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when creating a grant for a (namespace, grantee) pair that already has one. Grants are
/// additive, so a second row would only ever be a confusing duplicate of the first - the existing
/// grant's permissions are meant to be edited instead.
/// </summary>
public class NamespaceAccessGrantAlreadyExistsException(string grantee, string? namespaceName)
    : Exception(
        $"'{grantee}' already has an access grant for "
            + (namespaceName is null ? "all namespaces." : $"namespace '{namespaceName}'.")
    )
{
    public string Grantee { get; } = grantee;
    public string? NamespaceName { get; } = namespaceName;
}

/// <summary>Thrown when editing or deleting a grant that no longer exists.</summary>
public class NamespaceAccessGrantNotFoundException(Guid id)
    : Exception($"No access grant with the id '{id}' exists.")
{
    public Guid Id { get; } = id;
}
