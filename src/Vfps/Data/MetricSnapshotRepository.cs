using Microsoft.EntityFrameworkCore;

namespace Vfps.Data;

/// <inheritdoc/>
public class MetricSnapshotRepository(PseudonymContext context) : IMetricSnapshotRepository
{
    /// <inheritdoc/>
    public async Task WriteAsync(
        string name,
        IReadOnlyDictionary<string, long> values,
        CancellationToken cancellationToken
    )
    {
        var stored = new Dictionary<string, long>(values);
        var now = DateTimeOffset.UtcNow;

        await context
            .MetricSnapshots.Where(snapshot => snapshot.Name == name)
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(snapshot => snapshot.Values, stored)
                        .SetProperty(snapshot => snapshot.ComputedAt, now),
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
