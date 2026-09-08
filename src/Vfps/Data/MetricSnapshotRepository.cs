using Microsoft.EntityFrameworkCore;

namespace Vfps.Data;

/// <inheritdoc/>
public class MetricSnapshotRepository(PseudonymContext context) : IMetricSnapshotRepository
{
    /// <inheritdoc/>
    public async Task<bool> TryClaimRefreshAsync(
        string name,
        TimeSpan minimumAge,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - minimumAge;

        // ExecuteUpdateAsync rather than load-modify-SaveChanges: the point is that the read and
        // the write are one statement, so two replicas can't both observe a stale ComputedAt and
        // both decide to recompute. The row count tells the caller which of them won.
        var claimedRows = await context
            .MetricSnapshots.Where(snapshot =>
                snapshot.Name == name && snapshot.ComputedAt < cutoff
            )
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(snapshot => snapshot.ComputedAt, now),
                cancellationToken
            );

        return claimedRows == 1;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(
        string name,
        IReadOnlyDictionary<string, long> values,
        CancellationToken cancellationToken
    )
    {
        // ComputedAt is deliberately not touched here - TryClaimRefreshAsync already set it, and
        // it's the claim timestamp rather than a "last written" one.
        var stored = new Dictionary<string, long>(values);

        await context
            .MetricSnapshots.Where(snapshot => snapshot.Name == name)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(snapshot => snapshot.Values, stored),
                cancellationToken
            );
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, long>> ReadAsync(
        string name,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await context
            .MetricSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Name == name, cancellationToken);

        return snapshot?.Values ?? [];
    }
}
