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

    /// <summary>
    /// The authenticated user's email address, but only once the IdP reports it as verified -
    /// null otherwise, including when the IdP issues no email at all.
    /// </summary>
    /// <remarks>
    /// Read raw, for the same reason as <see cref="GetSubject"/>: "email" and "email_verified"
    /// are standard OIDC claims every provider spells the same way, unlike role claim naming
    /// (which is why <see cref="Config.AuthorizationConfig.RoleClaimType"/> exists), and
    /// <c>MapInboundClaims = false</c> keeps them from being rewritten to the long
    /// <see cref="ClaimTypes.Email"/> URI.
    ///
    /// Requiring the verification is what makes email-based
    /// <see cref="Data.Models.NamespaceAccessGrant"/>s safe to honour at all. Without it, an
    /// address is a self-asserted string: on a realm that permits self-registration or an
    /// unverified address change - the Keycloak default for self-registration - anyone could
    /// take over anyone else's grants by typing their address into a profile page. An IdP that
    /// issues no email_verified claim therefore yields no email identity here, and no email
    /// grant ever matches, which is the safe direction to fail in. Role grants are unaffected.
    /// </remarks>
    public static string? GetEmail(this ClaimsPrincipal user) =>
        IsEmailVerified(user) ? user.FindFirstValue("email") : null;

    /// <summary>
    /// Whether the IdP vouches for the email address it issued. Absent counts as unverified: the
    /// claim is only meaningful when it is there and true.
    /// </summary>
    /// <remarks>
    /// OIDC defines email_verified as a JSON boolean, which arrives here as the string "true" or
    /// "false" - <see cref="bool.TryParse(string, out bool)"/> accepts either in any casing, and
    /// treats anything else (a provider sending 1/0, say) as unverified rather than guessing.
    /// </remarks>
    private static bool IsEmailVerified(ClaimsPrincipal user) =>
        bool.TryParse(user.FindFirstValue("email_verified"), out var isVerified) && isVerified;

    /// <summary>
    /// The <see cref="Data.Models.ServiceAccount"/> this caller is, or null if it is a person -
    /// whether signed in through the browser, presenting an IdP-issued JWT, or presenting a
    /// personal access token, all three of which are the *user*, not a service account.
    /// </summary>
    public static string? GetServiceAccountName(this ClaimsPrincipal user) =>
        user.FindFirstValue(VfpsClaimTypes.ServiceAccount);

    /// <summary>
    /// Whether this request was authenticated with a vfps-issued access token rather than a
    /// browser session or an IdP-issued bearer token.
    /// </summary>
    /// <remarks>
    /// Used to refuse the one thing a token must not do: mint another one. A personal access
    /// token carries its owner's full identity, so every permission check it passes is one they
    /// would pass themselves - but letting it create tokens would let a leaked credential renew
    /// itself indefinitely, outliving the expiry that is the whole point of issuing it.
    /// </remarks>
    public static bool IsAccessTokenPrincipal(this ClaimsPrincipal user) =>
        user.HasClaim(claim => claim.Type == VfpsClaimTypes.TokenId);
}
