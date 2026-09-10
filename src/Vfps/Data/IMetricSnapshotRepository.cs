using Vfps.Data.Models;

namespace Vfps.Data;

/// <summary>
/// Reads and writes <see cref="MetricSnapshot"/> rows: the shared, database-backed store that lets
/// every replica export an expensive metric while only one of them computes it.
///
/// Which replica computes is settled by Hangfire's recurring job scheduler (see Program.cs), not by
/// anything in here - this interface is only the storage.
/// </summary>
public interface IMetricSnapshotRepository
{
    /// <summary>
    /// Replaces the stored values for <paramref name="name"/> and stamps
    /// <see cref="MetricSnapshot.ComputedAt"/> with the time of this write.
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
