namespace Vfps.Authorization;

/// <summary>
/// Records that a token was just used, without touching the database.
///
/// <see cref="Data.Models.AccessToken.LastUsedAt"/> is the one thing about a token that changes
/// on the request path, and it is also the least important - nothing reads it to make a decision.
/// Writing it inline would turn every authenticated call into a write against a row every other
/// replica has cached, so the handler only records it in memory here and
/// <see cref="AccessTokenUsageFlushBackgroundService"/> writes the accumulated timestamps back on
/// a timer. Repeated use of the same token in one interval collapses into a single update.
/// </summary>
public interface IAccessTokenUsageTracker
{
    void Track(Guid tokenId, DateTimeOffset usedAt);

    /// <summary>
    /// Takes everything accumulated so far, leaving the tracker empty. Returns an empty map when
    /// nothing has been used since the last call, which is the common case on an idle replica.
    /// </summary>
    IReadOnlyDictionary<Guid, DateTimeOffset> DrainPending();
}
