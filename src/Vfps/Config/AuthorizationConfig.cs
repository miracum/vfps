namespace Vfps.Config;

/// <summary>
/// The OIDC wiring, plus who counts as an admin (full access, incl. namespace create/delete).
///
/// Per-namespace access is deliberately *not* here: it lives in the database as
/// <see cref="Data.Models.NamespaceAccessGrant"/> rows that admins manage in the UI, so granting
/// a role - or one specific user, by email - access to a namespace doesn't need a config change
/// and a restart. <see cref="AdminRoles"/> stays configuration because it's the bootstrap:
/// somebody has to be an admin before there is any UI to grant anything from.
/// </summary>
public class AuthorizationConfig
{
    public bool IsEnabled { get; set; }

    /// <summary>The OIDC issuer/authority (e.g. a Keycloak realm URL).</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Audience the JWT bearer handler validates for gRPC/API callers.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Confidential client id/secret used for the Blazor UI's Authorization Code flow.</summary>
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Which claim type carries the caller's roles/groups.</summary>
    public string RoleClaimType { get; set; } = "roles";

    /// <summary>
    /// Whether to use RFC 9126 Pushed Authorization Requests when the authority's discovery
    /// document advertises support for them (ASP.NET Core's own default behavior). Some older
    /// IdPs - e.g. pre-Quarkus Keycloak (before ~v19) - advertise a PAR endpoint that doesn't
    /// correctly validate the redirect_uri parameter, breaking every login attempt with
    /// "invalid_request: Invalid parameter: redirect_uri". Set this to false against such an
    /// authority to fall back to the traditional front-channel authorize request.
    /// </summary>
    public bool UsePushedAuthorizationRequests { get; set; } = true;

    /// <summary>Roles granting full access: all namespaces, namespace create/delete.</summary>
    public List<string> AdminRoles { get; set; } = [];

    /// <summary>
    /// How long a replica may serve namespace access grants from its in-memory snapshot before
    /// re-reading them - see <see cref="Authorization.INamespaceAccessGrantCache"/>. The replica
    /// an admin makes a change on applies it immediately regardless; this only bounds how long
    /// the *other* replicas can still honour a grant that was just edited or revoked. Set to zero
    /// to read the grants on every check instead, at the cost of a database round trip per
    /// permission check (including one per pseudonym Create).
    /// </summary>
    public TimeSpan GrantCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a sign-in may keep serving an already-open admin UI tab before that tab is sent
    /// back through the login flow - see <see cref="Authorization.SessionLifetime"/> for why an
    /// open Blazor circuit needs a limit of its own at all.
    ///
    /// The trip through the IdP is silent whenever its own session is still valid, so the cost of
    /// a shorter value is a page reload, and what it buys is a shorter window in which a disabled
    /// account or a withdrawn admin role still works in a tab someone left open. Eight hours
    /// covers a working day without asking anyone to sign in twice. Set to zero to switch the
    /// limit off entirely, leaving the auth cookie's own expiry as the only bound.
    /// </summary>
    public TimeSpan MaxSessionAge { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// How often an open tab checks itself against <see cref="MaxSessionAge"/>. The check is a
    /// claim comparison with no I/O behind it, so this only decides how far past the limit a tab
    /// can run before it notices. Values below a second are clamped.
    /// </summary>
    public TimeSpan SessionRevalidationInterval { get; set; } = TimeSpan.FromMinutes(5);
}
