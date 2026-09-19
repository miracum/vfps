using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace Vfps.Authorization;

/// <inheritdoc/>
public sealed class AccessTokenUsageTracker : IAccessTokenUsageTracker
{
    private ConcurrentDictionary<Guid, DateTimeOffset> _pending = new();

    /// <inheritdoc/>
    public void Track(Guid tokenId, DateTimeOffset usedAt) =>
        // Last writer wins rather than max(): two requests in the same flush interval are at most
        // that interval apart, and the column is only ever read by a human wondering whether a
        // token is still in use.
        _pending[tokenId] = usedAt;

    /// <inheritdoc/>
    public IReadOnlyDictionary<Guid, DateTimeOffset> DrainPending()
    {
        if (_pending.IsEmpty)
        {
            return ReadOnlyDictionary<Guid, DateTimeOffset>.Empty;
        }

        // Swapped wholesale instead of drained key by key: a request authenticating during the
        // flush lands in the new dictionary and is written by the next one, rather than racing
        // with the removal of its own entry.
        return Interlocked.Exchange(ref _pending, new ConcurrentDictionary<Guid, DateTimeOffset>());
    }
}
