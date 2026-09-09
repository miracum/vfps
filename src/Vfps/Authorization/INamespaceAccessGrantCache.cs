using Vfps.Data.Models;

namespace Vfps.Authorization;

/// <summary>
/// A process-wide, short-lived snapshot of every <see cref="NamespaceAccessGrant"/> in the
/// database.
///
/// Authorization used to be pure in-memory configuration, so a permission check cost nothing;
/// now that grants are rows, an uncached check would add a database round trip to every single
/// pseudonym Create - the hottest path this service has. The whole grant set is tiny (same
/// low-cardinality reasoning as namespaces), so it's cached wholesale rather than queried per
/// namespace.
///
/// Writes through <see cref="AppServices.INamespaceAccessGrantAppService"/> call
/// <see cref="Invalidate"/>, so an admin's own change takes effect immediately on the replica
/// that made it; other replicas pick it up within
/// <see cref="Config.AuthorizationConfig.GrantCacheDuration"/>, which is what bounds how long a
/// revoked grant can still be honoured cluster-wide. Set that to zero to disable caching entirely.
/// </summary>
public interface INamespaceAccessGrantCache
{
    Task<IReadOnlyList<NamespaceAccessGrant>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Drops the cached snapshot so the next read goes back to the database.</summary>
    void Invalidate();
}
