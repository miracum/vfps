using Vfps.Data.Models;

namespace Vfps.Data;

/// <summary>
/// Store and retrieve the vfps-issued access tokens - see <see cref="AccessToken"/>.
/// </summary>
public interface IAccessTokenRepository
{
    /// <summary>
    /// Every token that has not been revoked, expired ones included. This is what
    /// <see cref="Authorization.IAccessTokenCache"/> snapshots: expiry is checked per request
    /// against the row (a snapshot that had filtered on "not expired" would be wrong the moment
    /// it aged), while revocation has to drop out of the set, since that is what makes it
    /// effective.
    /// </summary>
    Task<IReadOnlyList<AccessToken>> GetAllUnrevokedAsync(CancellationToken cancellationToken);

    /// <summary>Every token, revoked and expired ones included. For the admin listing.</summary>
    Task<IReadOnlyList<AccessToken>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Every token belonging to one user, by their <c>sub</c>.</summary>
    Task<IReadOnlyList<AccessToken>> GetBySubjectAsync(
        string subject,
        CancellationToken cancellationToken
    );

    /// <summary>Every token issued for one service account.</summary>
    Task<IReadOnlyList<AccessToken>> GetByServiceAccountAsync(
        string serviceAccountName,
        CancellationToken cancellationToken
    );

    Task<AccessToken?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<AccessToken> CreateAsync(AccessToken token, CancellationToken cancellationToken);

    /// <summary>Marks a token revoked. A no-op if it doesn't exist or is already revoked.</summary>
    Task RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken cancellationToken);

    /// <summary>Deletes a token row outright. A no-op if it doesn't exist.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Writes back the accumulated "last used" timestamps - see
    /// <see cref="Authorization.IAccessTokenUsageTracker"/>. Deliberately not a general update:
    /// this column is informational, written off the request path, and must never disturb the
    /// rest of a row.
    /// </summary>
    Task UpdateLastUsedAsync(
        IReadOnlyDictionary<Guid, DateTimeOffset> lastUsedByTokenId,
        CancellationToken cancellationToken
    );
}
