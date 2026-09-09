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

        var userRoles = GetUserRoles(user);
        var userEmail = user.GetEmail()?.ToLowerInvariant();

        var grants = await grantCache.GetAllAsync(cancellationToken);
        return new NamespacePermissions(
            grants.Where(grant => AppliesTo(grant, userRoles, userEmail))
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
        string? userEmail
    ) =>
        grant.GranteeType switch
        {
            GranteeType.Role => userRoles.Contains(grant.Grantee),
            // Grantee is stored already lower-cased (see NamespaceAccessGrantAppService), so the
            // claim side is all that needs normalizing - done once by the caller, not per grant.
            GranteeType.Email => userEmail is not null && userEmail == grant.Grantee,
            _ => false,
        };

    private HashSet<string> GetUserRoles(ClaimsPrincipal user) =>
        user.FindAll(Config.RoleClaimType).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
}
