using Vfps.Data.Models;

namespace Vfps.Data;

/// <summary>
/// Store and retrieve pseudonyms.
/// </summary>
public interface IPseudonymRepository
{
    /// <summary>
    /// Store the given pseudonym iff one with the same namespace, original value, and sequence
    /// number doesn't already exist. Every caller other than the multi-psn create path (see
    /// <see cref="AppServices.PseudonymAppService.CreateTrustedAsync(Models.Namespace, string, long, CancellationToken)"/>)
    /// always passes <see cref="Pseudonym.SequenceNumber"/> 0.
    /// </summary>
    /// <param name="pseudonym">The pseudonym to store</param>
    /// <returns>The newly stored pseudonym or the one fetched from the store if it already existed or null in case of an error.</returns>
    Task<Pseudonym?> CreateIfNotExist(Pseudonym pseudonym);

    /// <summary>
    /// Same as <see cref="CreateIfNotExist"/>, batched into a single round trip for many
    /// pseudonyms at once - CsvPseudonymizationJobRunner's dominant cost was one upsert round
    /// trip per field per row, which this collapses to one round trip per chunk. Callers must
    /// dedupe <paramref name="pseudonyms"/> by (NamespaceName, OriginalValue, SequenceNumber)
    /// first - passing the same key twice wastes a row rather than causing incorrect results.
    /// </summary>
    /// <returns>
    /// One entry per distinct (NamespaceName, OriginalValue, SequenceNumber) in
    /// <paramref name="pseudonyms"/>. Always fully covers the input - falls back to
    /// <see cref="CreateIfNotExist"/> one at a time for any key the batched round trip didn't
    /// return a row for (expected to be rare: only a concurrent writer racing the same key at
    /// the same instant).
    /// </returns>
    Task<IReadOnlyList<Pseudonym>> CreateIfNotExistBatchAsync(
        IReadOnlyList<Pseudonym> pseudonyms,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Fetches every pseudonym stored for a given (NamespaceName, OriginalValue), ordered by
    /// SequenceNumber ascending. At most one row for a namespace that never allows more than one
    /// pseudonym per original value; possibly several for a multi-psn namespace (see
    /// <see cref="Models.Namespace.AllowsMultiplePseudonyms"/>).
    /// </summary>
    Task<IReadOnlyList<Pseudonym>> FindAllByOriginalValueAsync(
        string namespaceName,
        string originalValue,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Inserts <paramref name="newSequenceCandidates"/> (the missing sequence numbers a multi-psn
    /// Create call decided to add - see <see cref="AppServices.PseudonymAppService.CreateTrustedAsync(Models.Namespace, string, long, CancellationToken)"/>)
    /// iff they don't already exist, then returns the complete, up-to-date set of every
    /// pseudonym stored for that (NamespaceName, OriginalValue) - not just the candidates just
    /// inserted. That final fresh read is what makes this correct under a concurrent race: two
    /// callers computing overlapping "missing" sets from a stale read and inserting at the same
    /// time both end up seeing whatever actually got persisted, rather than each returning its
    /// own possibly-incomplete candidate list.
    /// </summary>
    Task<IReadOnlyList<Pseudonym>> CreateSetIfNotExistAsync(
        IReadOnlyList<Pseudonym> newSequenceCandidates,
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
    /// Offset-paged, case-insensitive substring search within a namespace, ordered the same as
    /// <see cref="ListByNamespaceAsync"/>. Always pays for a total count alongside the page
    /// itself - unlike <see cref="CountByNamespaceAsync"/>, that cost isn't optional here, since
    /// the caller (the Blazor pseudonym list page) needs it to render "page X of Y". A substring
    /// scan can't use a plain b-tree index, so this is a full scan of the namespace at any scale
    /// - acceptable for an admin-facing per-namespace browse, not meant for a hot path.
    /// <paramref name="searchText"/> is matched against <c>pseudonym_value</c> (and, when
    /// <paramref name="includeOriginalValueInSearch"/> is true, <c>original_value</c>) as a
    /// substring, case-insensitively; null or blank returns every row in the namespace,
    /// unfiltered. Callers must only pass <paramref name="includeOriginalValueInSearch"/> true
    /// when the caller already has reverse-lookup access to the namespace - matching against
    /// original values is itself a reverse-lookup-shaped capability (it lets a caller confirm an
    /// original value is present without ever seeing it returned), so this repository trusts the
    /// caller to have already gated it. See
    /// <see cref="AppServices.IPseudonymAppService.SearchAsync"/>.
    /// </summary>
    Task<(IReadOnlyList<Pseudonym> Items, long TotalCount)> SearchByNamespaceAsync(
        string namespaceName,
        string? searchText,
        bool includeOriginalValueInSearch,
        int skip,
        int take,
        CancellationToken cancellationToken
    );

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
