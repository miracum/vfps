using System.Security.Claims;

namespace Vfps.Authorization;

/// <summary>
/// Resolves a caller's namespace-scoped permissions from <see cref="Config.AuthorizationConfig"/>.
/// Used directly by app-service methods (<see cref="AppServices.NamespaceAppService"/>,
/// <see cref="AppServices.PseudonymAppService"/>) so every caller - gRPC, Blazor, and (later) the
/// CSV job runner and file endpoints - is covered uniformly, regardless of transport.
///
/// Every check returns true when authorization is disabled (<c>Authorization:IsEnabled=false</c>),
/// matching this codebase's existing off-by-default idiom for optional features.
/// </summary>
public interface INamespacePermissionChecker
{
    bool IsAdmin(ClaimsPrincipal user);
    bool HasReadAccess(ClaimsPrincipal user, string namespaceName);
    bool HasWriteAccess(ClaimsPrincipal user, string namespaceName);
    bool HasReverseLookupAccess(ClaimsPrincipal user, string namespaceName);
}

/// <summary>Thrown when a caller lacks the permission required for an operation.</summary>
public class ForbiddenException(string message) : Exception(message);
