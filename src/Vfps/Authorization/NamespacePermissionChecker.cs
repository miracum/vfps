using System.Security.Claims;
using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.Data.Models;

namespace Vfps.Authorization;

/// <inheritdoc/>
public class NamespacePermissionChecker(
    IOptions<AuthorizationConfig> options,
    INamespaceAccessGrantCache grantCache
) : INamespacePermissionChecker
{
    private AuthorizationConfig Config => options.Value;

    /// <inheritdoc/>
    public bool IsAdmin(ClaimsPrincipal user)
    {
        if (!Config.IsEnabled)
        {
            return true;
        }

        // A service account is never an admin, whatever it is granted and whatever the token's
        // principal claims. Its whole authority is the grants an admin made to it, so there is no
        // path by which a leaked service-account token can create or delete a namespace, manage
        // grants, or reach the Hangfire dashboard. Checked before the role test rather than
        // relying on the principal simply carrying no roles: this is a guarantee, not a
        // side effect of how AccessTokenAuthenticationHandler happens to build the principal.
        if (user.GetServiceAccountName() is not null)
        {
            return false;
        }

        var userRoles = GetUserRoles(user);
        return Config.AdminRoles.Any(userRoles.Contains);
    }

    /// <inheritdoc/>
    public async Task<NamespacePermissions> ResolveAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        // Neither case has anything to resolve against the grant table, so neither touches the
        // database at all: with authorization off everyone has full access, and an admin already
        // has it everywhere.
        if (!Config.IsEnabled || IsAdmin(user))
        {
            return NamespacePermissions.Unrestricted;
        }

        var serviceAccountName = user.GetServiceAccountName();

        // A service account is resolved against service-account grants and nothing else. Its
        // principal carries neither roles nor an email to begin with (see
        // AccessTokenAuthenticationHandler), so this changes no real request - it is here so the
        // separation is a property of the authorization code rather than of how one particular
        // handler happens to build a principal.
        var userRoles = serviceAccountName is null ? GetUserRoles(user) : [];
        var userEmail = serviceAccountName is null ? user.GetEmail()?.ToLowerInvariant() : null;

        var grants = await grantCache.GetAllAsync(cancellationToken);
        return new NamespacePermissions(
            grants.Where(grant => AppliesTo(grant, userRoles, userEmail, serviceAccountName))
        );
    }

    /// <inheritdoc/>
    public async Task<bool> HasReadAccessAsync(
        ClaimsPrincipal user,
        string namespaceName,
        CancellationToken cancellationToken
    ) => (await ResolveAsync(user, cancellationToken)).HasReadAccess(namespaceName);

    /// <inheritdoc/>
    public async Task<bool> HasWriteAccessAsync(
        ClaimsPrincipal user,
        string namespaceName,
        CancellationToken cancellationToken
    ) => (await ResolveAsync(user, cancellationToken)).HasWriteAccess(namespaceName);

    /// <inheritdoc/>
    public async Task<bool> HasReverseLookupAccessAsync(
        ClaimsPrincipal user,
        string namespaceName,
        CancellationToken cancellationToken
    ) => (await ResolveAsync(user, cancellationToken)).HasReverseLookupAccess(namespaceName);

    private static bool AppliesTo(
        NamespaceAccessGrant grant,
        HashSet<string> userRoles,
        string? userEmail,
        string? serviceAccountName
    ) =>
        grant.GranteeType switch
        {
            GranteeType.Role => userRoles.Contains(grant.Grantee),
            // Grantee is stored already lower-cased (see NamespaceAccessGrantAppService), so the
            // claim side is all that needs normalizing - done once by the caller, not per grant.
            GranteeType.Email => userEmail is not null && userEmail == grant.Grantee,
            // Only ever non-null for a service-account access token, so a person can never match
            // one of these grants and a service account can never match the other two.
            GranteeType.ServiceAccount => serviceAccountName is not null
                && serviceAccountName == grant.Grantee,
            _ => false,
        };

    private HashSet<string> GetUserRoles(ClaimsPrincipal user) =>
        user.FindAll(Config.RoleClaimType).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
}
