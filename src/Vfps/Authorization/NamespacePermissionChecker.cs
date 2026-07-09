using System.Security.Claims;
using Microsoft.Extensions.Options;
using Vfps.Config;

namespace Vfps.Authorization;

/// <inheritdoc/>
public class NamespacePermissionChecker(IOptions<AuthorizationConfig> options)
    : INamespacePermissionChecker
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
    public bool HasReadAccess(ClaimsPrincipal user, string namespaceName) =>
        !Config.IsEnabled || IsAdmin(user) || HasAnyRole(user, namespaceName, r => r.ReadRoles);

    /// <inheritdoc/>
    public bool HasWriteAccess(ClaimsPrincipal user, string namespaceName) =>
        !Config.IsEnabled || IsAdmin(user) || HasAnyRole(user, namespaceName, r => r.WriteRoles);

    /// <inheritdoc/>
    public bool HasReverseLookupAccess(ClaimsPrincipal user, string namespaceName) =>
        !Config.IsEnabled
        || IsAdmin(user)
        || HasAnyRole(user, namespaceName, r => r.ReverseLookupRoles);

    private bool HasAnyRole(
        ClaimsPrincipal user,
        string namespaceName,
        Func<NamespaceRule, List<string>> roleSelector
    )
    {
        var userRoles = GetUserRoles(user);

        return Config
            .NamespaceRules.Where(r => r.Namespace == "*" || r.Namespace == namespaceName)
            .SelectMany(roleSelector)
            .Any(userRoles.Contains);
    }

    private HashSet<string> GetUserRoles(ClaimsPrincipal user) =>
        user.FindAll(Config.RoleClaimType).Select(c => c.Value).ToHashSet();
}
