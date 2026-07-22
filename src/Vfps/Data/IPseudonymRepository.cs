using Vfps.Data.Models;

namespace Vfps.Data
{
    /// <summary>
    /// Store and retrieve pseudonyms.
    /// </summary>
    public interface IPseudonymRepository
    {
        /// <summary>
        /// Store the given pseudonym iff one with the same namespace and original value doesn't already exist.
        /// </summary>
        /// <param name="pseudonym">The pseudonym to store</param>
        /// <returns>The newly stored pseudonym or the one fetched from the store if it already existed or null in case of an error.</returns>
        Task<Pseudonym?> CreateIfNotExist(Pseudonym pseudonym);

        /// <summary>
        /// Same as <see cref="CreateIfNotExist"/>, batched into a single round trip for many
        /// pseudonyms at once - CsvPseudonymizationJobRunner's dominant cost was one upsert round
        /// trip per field per row, which this collapses to one round trip per chunk. Callers must
        /// dedupe <paramref name="pseudonyms"/> by (NamespaceName, OriginalValue) first - passing
        /// the same key twice wastes a row rather than causing incorrect results.
        /// </summary>
        /// <returns>
        /// One entry per distinct (NamespaceName, OriginalValue) in <paramref name="pseudonyms"/>.
        /// Always fully covers the input - falls back to <see cref="CreateIfNotExist"/> one at a
        /// time for any key the batched round trip didn't return a row for (expected to be rare:
        /// only a concurrent writer racing the same key at the same instant).
        /// </returns>
        Task<IReadOnlyList<Pseudonym>> CreateIfNotExistBatchAsync(
            IReadOnlyList<Pseudonym> pseudonyms,
            CancellationToken cancellationToken
        );

        /// <summary>
        /// Lists pseudonyms in a namespace via keyset/seek pagination, ordered by
        /// (created_at DESC, original_value DESC). Pass the last item of the previous page as
        /// <paramref name="cursor"/> to get the next page, or null to get the first page.
        /// </summary>
        Task<IReadOnlyList<Pseudonym>> ListByNamespaceAsync(
            string namespaceName,
            PseudonymPageCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken
        );

        /// <summary>
        /// Counts all pseudonyms in a namespace. This is a full-scan-class operation at scale -
        /// only call it when a caller has explicitly opted into paying for a total count.
        /// </summary>
        Task<long> CountByNamespaceAsync(string namespaceName, CancellationToken cancellationToken);

        /// <summary>
        /// Counts all pseudonyms, grouped by namespace, in one query. Same full-scan-class cost
        /// as <see cref="CountByNamespaceAsync"/> (across every namespace instead of one) - only
        /// called by <see cref="PseudonymCountMetricsBackgroundService"/>'s periodic metrics
        /// refresh, never on a request path. A namespace with zero pseudonyms is simply absent
        /// from the result rather than present with a zero count.
        /// </summary>
        Task<IReadOnlyDictionary<string, long>> CountAllGroupedByNamespaceAsync(
            CancellationToken cancellationToken
        );

        /// <summary>
        /// Reverse lookup: finds a pseudonym by its pseudonym_value (revealing original_value).
        /// Backed by the (namespace_name, pseudonym_value) index - see
        /// AddPseudonymKeysetAndReverseLookupIndexes.
        /// </summary>
        Task<Pseudonym?> FindByPseudonymValueAsync(
            string namespaceName,
            string pseudonymValue,
            CancellationToken cancellationToken
        );
    }
}
