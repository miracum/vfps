using Vfps.Data.Models;

namespace Vfps.Authorization;

/// <summary>
/// A process-wide, short-lived snapshot of every live <see cref="AccessToken"/>, keyed by
/// <see cref="AccessToken.TokenId"/>.
///
/// Exists for the same reason <see cref="INamespaceAccessGrantCache"/> does, one step earlier in
/// the request: authenticating a token is a database lookup, and doing it per request would put
/// a round trip in front of every pseudonym Create made by a token-authenticated client - on top
/// of the one the permission check already avoids. The token set is small (one row per issued
/// credential), so it is cached wholesale.
///
/// Writes through <see cref="AppServices.IAccessTokenAppService"/> call <see cref="Invalidate"/>,
/// so a revocation takes effect immediately on the replica that made it; other replicas pick it
/// up within <see cref="Config.AuthorizationConfig.GrantCacheDuration"/>, exactly as for grants.
/// Expiry needs no invalidation - it is checked against the row on every request.
/// </summary>
public interface IAccessTokenCache
{
    /// <summary>
    /// The unrevoked token with this id, or null if there is none. May be expired - the caller
    /// checks, since a snapshot can age past a token's expiry.
    /// </summary>
    Task<AccessToken?> FindAsync(string tokenId, CancellationToken cancellationToken);

    /// <summary>Drops the cached snapshot so the next read goes back to the database.</summary>
    void Invalidate();
}
