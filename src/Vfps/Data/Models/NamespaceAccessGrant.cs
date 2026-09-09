using System.ComponentModel.DataAnnotations;

namespace Vfps.Data.Models;

/// <summary>
/// Who a <see cref="NamespaceAccessGrant"/> applies to.
/// </summary>
public enum GranteeType
{
    /// <summary>
    /// An OIDC role/group, matched against the caller's
    /// <see cref="Config.AuthorizationConfig.RoleClaimType"/> claims. Matched case-sensitively,
    /// since that's how an IdP emits them.
    /// </summary>
    Role,

    /// <summary>
    /// A single user's email address, matched against the caller's "email" claim regardless of
    /// which roles they hold. Stored and matched lower-cased.
    /// </summary>
    Email,
}

/// <summary>
/// One access rule: grants a role or an individual user read, write and/or reverse-lookup access
/// to one namespace - or, when <see cref="NamespaceName"/> is null, to every namespace.
///
/// Managed entirely through the admin UI (Components/Pages/AccessControl.razor), not
/// configuration - <see cref="Config.AuthorizationConfig"/> only carries the OIDC wiring and the
/// bootstrap <see cref="Config.AuthorizationConfig.AdminRoles"/>, since somebody has to be able to
/// sign in and create the first grant.
///
/// Grants are additive and never subtractive: a caller's effective permissions are the union of
/// every grant matching one of their roles or their email address. There is no deny rule - the
/// absence of a grant is the denial.
/// </summary>
public class NamespaceAccessGrant : TracksCreationAndUpdates
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The namespace this grant applies to, or null for "every namespace" - the database-backed
    /// equivalent of the old <c>Authorization:NamespaceRules</c> <c>"*"</c> wildcard. Deliberately
    /// not inherited down a parent/child namespace hierarchy: like the generation configuration a
    /// namespace carries, access is set per namespace (see docs/design/multi-level-namespaces.md).
    /// </summary>
    public string? NamespaceName { get; set; }

    public GranteeType GranteeType { get; set; }

    /// <summary>The role name or email address, depending on <see cref="GranteeType"/>.</summary>
    public required string Grantee { get; set; }

    /// <summary>Namespace browsing and pseudonym listing (without original values).</summary>
    public bool CanRead { get; set; }

    /// <summary>Creating pseudonyms in the namespace.</summary>
    public bool CanWrite { get; set; }

    /// <summary>
    /// Revealing a pseudonym's original value. A separate, more tightly-scoped grant than
    /// <see cref="CanRead"/> - read access alone never reveals original values.
    /// </summary>
    public bool CanReverseLookup { get; set; }

    public Namespace? Namespace { get; set; }
}
