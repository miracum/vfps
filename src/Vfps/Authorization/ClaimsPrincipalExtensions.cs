using System.Security.Claims;

namespace Vfps.Authorization;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The authenticated user's stable identifier, or "anonymous" if there isn't one.
    /// </summary>
    /// <remarks>
    /// Reads the raw OIDC/JWT "sub" claim rather than <see cref="ClaimTypes.NameIdentifier"/> -
    /// Program.cs sets <c>MapInboundClaims = false</c> on both the OIDC and JWT bearer handlers
    /// so that <see cref="Config.AuthorizationConfig.RoleClaimType"/> matches the IdP's raw claim
    /// name unchanged, and the same setting also stops the short "sub" claim from being
    /// auto-mapped to the long ClaimTypes.NameIdentifier URI. "sub" is a claim every OIDC-
    /// compliant provider issues (unlike role claim naming, which varies by IdP), so it's safe to
    /// read directly rather than through a configurable claim type.
    /// </remarks>
    public static string GetSubject(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? "anonymous";
}
