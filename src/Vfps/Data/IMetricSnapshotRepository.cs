using Vfps.Data.Models;

namespace Vfps.Data;

/// <summary>
/// Reads and writes <see cref="MetricSnapshot"/> rows: the shared, database-backed store that lets
/// every replica export an expensive metric while only one of them computes it.
/// </summary>
public interface IMetricSnapshotRepository
{
    /// <summary>
    /// Atomically takes the right to recompute <paramref name="name"/>, if its last recompute was
    /// longer ago than <paramref name="minimumAge"/>.
    ///
    /// A single conditional UPDATE, so of any number of replicas calling this at the same moment
    /// exactly one gets <c>true</c> - the row either matched the age predicate for a caller or had
    /// already been bumped by whoever got there first. That's the whole coordination mechanism:
    /// no advisory locks, no explicit transaction (which the retrying execution strategy would
    /// reject anyway), and nothing to release if the winner then crashes.
    /// </summary>
    /// <returns><c>true</c> if this caller should recompute and then call
    /// <see cref="WriteAsync"/>; <c>false</c> if another replica has it.</returns>
    Task<bool> TryClaimRefreshAsync(
        string name,
        TimeSpan minimumAge,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Replaces the stored values for <paramref name="name"/>. Only meaningful after
    /// <see cref="TryClaimRefreshAsync"/> returned <c>true</c> for this caller.
    /// </summary>
    Task WriteAsync(
        string name,
        IReadOnlyDictionary<string, long> values,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Reads the current values for <paramref name="name"/>. Cheap enough for every replica to call
    /// on a short interval - one row, and metric cardinality here is the namespace count.
    /// </summary>
    Task<IReadOnlyDictionary<string, long>> ReadAsync(
        string name,
        CancellationToken cancellationToken
    );
}
