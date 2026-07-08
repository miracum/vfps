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
