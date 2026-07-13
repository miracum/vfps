namespace Vfps.Config;

/// <summary>
/// Declarative, config-driven authorization: who's an admin (full access, incl. namespace
/// create/delete) and which OIDC roles/groups grant read/write/reverse-lookup access to which
/// namespaces. No database-backed grant table or admin UI for managing this - deliberately out
/// of scope for now.
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

    /// <summary>Roles granting full access: all namespaces, namespace create/delete.</summary>
    public List<string> AdminRoles { get; set; } = [];

    public List<NamespaceRule> NamespaceRules { get; set; } = [];
}

/// <summary>
/// Grants for one namespace (or all namespaces, via <see cref="Namespace"/> = "*").
/// </summary>
public class NamespaceRule
{
    public required string Namespace { get; set; }
    public List<string> ReadRoles { get; set; } = [];
    public List<string> WriteRoles { get; set; } = [];
    public List<string> ReverseLookupRoles { get; set; } = [];
}
