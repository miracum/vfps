using System.Security.Claims;

namespace Vfps.Authorization;

/// <summary>
/// Resolves a caller's namespace-scoped permissions.
///
/// Admin status comes from <see cref="Config.AuthorizationConfig.AdminRoles"/> - it stays
/// configuration because it's the bootstrap: somebody has to be able to sign in and create the
/// first grant. Everything else comes from the
/// <see cref="Data.Models.NamespaceAccessGrant"/> rows admins manage in the UI, which is why
/// those checks are async while <see cref="IsAdmin"/> isn't.
///
/// Used directly by app-service methods (<see cref="AppServices.NamespaceAppService"/>,
/// <see cref="AppServices.PseudonymAppService"/>, <see cref="AppServices.PseudonymizationJobAppService"/>)
/// so every caller - gRPC, Blazor, the CSV job runner and file endpoints - is covered uniformly,
/// regardless of transport.
///
/// Every check returns true when authorization is disabled (<c>Authorization:IsEnabled=false</c>),
/// matching this codebase's existing off-by-default idiom for optional features.
/// </summary>
public interface INamespacePermissionChecker
{
    bool IsAdmin(ClaimsPrincipal user);

    /// <summary>
    /// Resolves the caller's permissions across every namespace at once. Prefer this over
    /// repeated single-namespace calls whenever more than one namespace is being checked (listing
    /// namespaces, validating a CSV job's column mappings): it reads the grant set once and then
    /// answers per-namespace questions in memory, and every answer comes from the same consistent
    /// snapshot.
    /// </summary>
    Task<NamespacePermissions> ResolveAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    Task<bool> HasReadAccessAsync(
        ClaimsPrincipal user,
        string namespaceName,
        CancellationToken cancellationToken
    );

    Task<bool> HasWriteAccessAsync(
        ClaimsPrincipal user,
        string namespaceName,
        CancellationToken cancellationToken
    );

    Task<bool> HasReverseLookupAccessAsync(
        ClaimsPrincipal user,
        string namespaceName,
        CancellationToken cancellationToken
    );
}

/// <summary>Thrown when a caller lacks the permission required for an operation.</summary>
public class ForbiddenException(string message) : Exception(message);
