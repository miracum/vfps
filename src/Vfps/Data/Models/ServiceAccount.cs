using System.ComponentModel.DataAnnotations;

namespace Vfps.Data.Models;

/// <summary>
/// A non-human principal: a name an admin can grant namespace access to, and that one or more
/// <see cref="AccessToken"/>s authenticate as.
///
/// The account, not the token, is the grantee - <see cref="NamespaceAccessGrant"/> rows point at
/// <see cref="Name"/> via <see cref="GranteeType.ServiceAccount"/>. That separation is what makes
/// rotation possible: issue a second token, roll it out, revoke the first, and the grants never
/// move. It is also what keeps a service account's access visible in the same place as everyone
/// else's, instead of hidden inside a credential.
///
/// A service account is never an admin, whatever it is granted - see
/// <see cref="Authorization.NamespacePermissionChecker.IsAdmin"/>.
/// </summary>
public class ServiceAccount : TracksCreationAndUpdates
{
    /// <summary>
    /// The account's name and primary key, e.g. <c>etl-pipeline</c>. Lower-case, and immutable:
    /// grants reference it by value (there is no foreign key to hang a rename off, since
    /// <see cref="NamespaceAccessGrant.Grantee"/> holds role names and email addresses too), so
    /// a rename would silently strand every grant made to the old name.
    /// </summary>
    [Key]
    public required string Name { get; set; }

    /// <summary>What this account is for - shown in the admin UI, never used for matching.</summary>
    public string? Description { get; set; }

    /// <summary>The <c>sub</c> of the admin who created it, for audit.</summary>
    public required string CreatedBy { get; set; }

    public ICollection<AccessToken> Tokens { get; set; } = [];
}
