using System.Globalization;
using System.Security.Claims;

namespace Vfps.Authorization;

/// <summary>
/// Bounds how long an open Blazor Server circuit may go on serving the identity it was created
/// with.
///
/// A circuit is handed a <see cref="ClaimsPrincipal"/> when it connects and nothing re-checks it
/// afterwards: the auth cookie is validated per HTTP request, and a connected circuit makes none -
/// it is one long-lived WebSocket. So an open tab keeps working after the account behind it is
/// disabled at the IdP, after the user signs out somewhere else, and after an admin role is taken
/// away, none of which this app would otherwise notice until the tab is closed. Per-namespace
/// access grants are the exception and already fine: those live in the database and are re-read
/// within <see cref="Config.AuthorizationConfig.GrantCacheDuration"/>, so revoking one takes
/// effect on its own.
///
/// The check is deliberately a plain age limit rather than a call back to the IdP. A circuit has
/// no token to introspect with - SaveTokens keeps those in the auth cookie's properties, which
/// are reachable from an HTTP request and not from inside a circuit - and getting one in there
/// means capturing it during prerender and then holding a bearer token in circuit memory for the
/// lifetime of the tab, which trades one exposure for another.
/// </summary>
internal static class SessionLifetime
{
    /// <summary>
    /// When the sign-in behind this principal happened, as Unix seconds.
    ///
    /// Stamped by the app at sign-in (see Program.cs) rather than read off the token, because the
    /// principal that reaches the cookie carries no timestamp of its own: ASP.NET Core's OIDC
    /// handler deletes iat/nbf/exp from it by default, and auth_time is optional in OIDC and not
    /// emitted here. What survives is sub, sid, roles, name, email and the like - verified
    /// against the realm in tests/keycloak.
    /// </summary>
    internal const string SignedInAtClaimType = "vfps:signed_in_at";

    public static Claim StampFor(DateTimeOffset signedInAt) =>
        new(
            SignedInAtClaimType,
            signedInAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        );

    /// <returns>
    /// <c>true</c> when a circuit must stop trusting this principal and send the browser back
    /// through the login flow.
    /// </returns>
    public static bool HasExpired(ClaimsPrincipal user, TimeSpan maxAge, DateTimeOffset now)
    {
        if (maxAge <= TimeSpan.Zero)
        {
            return false;
        }

        var signedInAt = user.FindFirstValue(SignedInAtClaimType);
        if (
            !long.TryParse(
                signedInAt,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var unixSeconds
            )
        )
        {
            // No stamp, or an unreadable one: a cookie minted before this app started adding it,
            // or by some path that doesn't. Left valid rather than signed out where it stands -
            // the alternative turns every session that predates a deployment into an immediate
            // redirect, and the cookie's own expiry still bounds those.
            return false;
        }

        return now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds) >= maxAge;
    }
}
