using Vfps.Data.Models;

namespace Vfps.Data;

/// <summary>
/// Reads and writes <see cref="PseudonymCount"/> rows: the shared store that lets every replica
/// export the per-namespace pseudonym count while only one of them computes it.
///
/// Which replica computes is settled by Hangfire's recurring job scheduler (see Program.cs), not by
/// anything in here - this interface is only the storage.
/// </summary>
public interface IPseudonymCountRepository
{
    /// <summary>
    /// Replaces every stored count with <paramref name="counts"/>, stamping each row with
    /// <paramref name="computedAt"/>.
    ///
    /// Wholesale, not a merge: a namespace absent from <paramref name="counts"/> has its row
    /// removed rather than left at its last value, which is what lets a namespace that no longer
    /// has any pseudonyms actually stop being exported.
    /// </summary>
    Task ReplaceAllAsync(
        IReadOnlyDictionary<string, long> counts,
        DateTimeOffset computedAt,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Reads the current counts, keyed by namespace. Cheap enough for every replica to call on a
    /// short interval - one small row per namespace, and namespace cardinality is low by design.
    /// </summary>
    Task<IReadOnlyDictionary<string, long>> GetAllAsync(CancellationToken cancellationToken);
}
