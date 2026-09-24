using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.Authorization;

/// <inheritdoc/>
public sealed class NamespaceAccessGrantCache(
    INamespaceAccessGrantRepository grantRepository,
    IOptions<AuthorizationConfig> options
) : INamespaceAccessGrantCache, IDisposable
{
    // Only one caller reloads on expiry; the rest wait for that load rather than each issuing
    // their own query. Relevant because expiry tends to be hit by many concurrent gRPC calls at
    // once, not by one.
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    // A single immutable record swapped in as a whole, rather than separate list/timestamp
    // fields: only reference assignment is atomic, so this is what keeps a reader from ever
    // pairing a fresh list with a stale timestamp.
    private Snapshot? _snapshot;

    private TimeSpan CacheDuration => options.Value.GrantCacheDuration;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<NamespaceAccessGrant>> GetAllAsync(
        CancellationToken cancellationToken
    )
    {
        if (TryGetFresh(out var cached))
        {
            return cached;
        }

        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            // Re-checked inside the lock: whoever held it while this call was queued has already
            // done the reload this call was waiting for.
            if (TryGetFresh(out cached))
            {
                return cached;
            }

            var grants = await grantRepository.GetAllAsync(cancellationToken);

            Volatile.Write(ref _snapshot, new Snapshot(grants, DateTimeOffset.UtcNow));
            return grants;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    /// <inheritdoc/>
    public void Invalidate() => Volatile.Write(ref _snapshot, null);

    private bool TryGetFresh(out IReadOnlyList<NamespaceAccessGrant> grants)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var duration = CacheDuration;

        if (
            snapshot is null
            || duration <= TimeSpan.Zero
            || DateTimeOffset.UtcNow - snapshot.LoadedAt >= duration
        )
        {
            grants = [];
            return false;
        }

        grants = snapshot.Grants;
        return true;
    }

    public void Dispose() => _reloadLock.Dispose();

    private sealed record Snapshot(
        IReadOnlyList<NamespaceAccessGrant> Grants,
        DateTimeOffset LoadedAt
    );
}
