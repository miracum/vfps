using System.Security.Claims;
using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <summary>
/// Pseudonym operations shared by the gRPC adapter (<see cref="Services.PseudonymService"/>) and
/// Blazor Server components. See <see cref="INamespaceAppService"/> for why every method takes
/// the caller's <see cref="ClaimsPrincipal"/> explicitly.
/// </summary>
public interface IPseudonymAppService
{
    /// <summary>
    /// Creates (or fetches/grows the existing) set of pseudonyms for <paramref name="originalValue"/>
    /// in <paramref name="namespaceName"/>. Requires write access to the namespace. Shared by the
    /// gRPC adapter and <see cref="CsvProcessing.CsvPseudonymizationJobRunner"/>, so both paths
    /// get identical generation logic and per-namespace write-access enforcement.
    /// </summary>
    /// <param name="namespaceName">The namespace to create the pseudonym(s) in.</param>
    /// <param name="originalValue">The value to pseudonymize.</param>
    /// <param name="count">
    /// How many distinct pseudonyms the caller wants stored for <paramref name="originalValue"/>.
    /// Must be at least 1; anything above 1 requires the namespace's
    /// <see cref="Namespace.AllowsMultiplePseudonyms"/> to be set. If fewer than <paramref name="count"/>
    /// already exist, exactly the missing ones are generated and added (existing ones are never
    /// regenerated); if this many or more already exist, the existing set is returned unchanged -
    /// a given original value's stored set only ever grows, so a repeat call with the same or a
    /// smaller <paramref name="count"/> is always a no-op.
    /// </param>
    /// <param name="user">The caller, checked for write access to <paramref name="namespaceName"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every pseudonym stored for <paramref name="originalValue"/> after this call, ordered by sequence number.</returns>
    /// <exception cref="ArgumentException"><paramref name="originalValue"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than 1.</exception>
    /// <exception cref="MultiplePseudonymsNotAllowedException">
    /// <paramref name="count"/> is greater than 1 but the namespace doesn't allow multiple pseudonyms.
    /// </exception>
    /// <exception cref="OriginalValueValidationException">
    /// <paramref name="originalValue"/> does not match the namespace's
    /// <see cref="Namespace.OriginalValueValidationRegex"/>.
    /// </exception>
    Task<IReadOnlyList<Pseudonym>> CreateAsync(
        string namespaceName,
        string originalValue,
        long count,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Same as <see cref="CreateAsync"/> but skips the per-call permission check - only for the
    /// CSV job runner, which already verified write access to every namespace a job's column
    /// mappings reference up front, at job creation time (see
    /// <see cref="IPseudonymizationJobAppService.CreateJobAsync"/>), before any row processing
    /// began. The runner has no caller <see cref="ClaimsPrincipal"/> to re-check against - it
    /// runs later, in a Hangfire background thread, well after the request that created the job.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="originalValue"/> is blank.</exception>
    /// <exception cref="OriginalValueValidationException">
    /// <paramref name="originalValue"/> does not match the namespace's
    /// <see cref="Namespace.OriginalValueValidationRegex"/>.
    /// </exception>
    Task<Pseudonym> CreateTrustedAsync(
        string namespaceName,
        string originalValue,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Same as <see cref="CreateTrustedAsync(string, string, CancellationToken)"/>, but skips the
    /// namespace lookup too - only for the CSV job runner, which resolves each distinct namespace
    /// its column mappings reference exactly once before processing any rows, rather than
    /// re-fetching the same namespace on every field of every row (the dominant per-row cost
    /// otherwise, since a CSV job calls this far more often than any other caller ever would).
    /// Single-value convenience wrapper around
    /// <see cref="CreateTrustedAsync(Namespace, string, long, CancellationToken)"/> with
    /// <c>count: 1</c> - always returns the first (sequence number 0) pseudonym, ignoring any
    /// others a multi-psn namespace might already have stored for this original value.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="originalValue"/> is blank.</exception>
    /// <exception cref="OriginalValueValidationException">
    /// <paramref name="originalValue"/> does not match the namespace's
    /// <see cref="Namespace.OriginalValueValidationRegex"/>.
    /// </exception>
    Task<Pseudonym> CreateTrustedAsync(
        Namespace @namespace,
        string originalValue,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Same trust boundary as <see cref="CreateTrustedAsync(Namespace, string, CancellationToken)"/>,
    /// generalized to a <paramref name="count"/> of pseudonyms - the core multi-psn create/grow
    /// logic. See <see cref="CreateAsync"/> for the grow-to-N semantics; this is that same logic
    /// without the permission check.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="originalValue"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than 1.</exception>
    /// <exception cref="MultiplePseudonymsNotAllowedException">
    /// <paramref name="count"/> is greater than 1 but the namespace doesn't allow multiple pseudonyms.
    /// </exception>
    /// <exception cref="OriginalValueValidationException">
    /// <paramref name="originalValue"/> does not match the namespace's
    /// <see cref="Namespace.OriginalValueValidationRegex"/>.
    /// </exception>
    Task<IReadOnlyList<Pseudonym>> CreateTrustedAsync(
        Namespace @namespace,
        string originalValue,
        long count,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Same trust boundary as <see cref="CreateTrustedAsync(Namespace, string, CancellationToken)"/>,
    /// batched into a single database round trip for many values at once - only for the CSV job
    /// runner, whose dominant cost was one upsert round trip per field per row. Not exposed via
    /// gRPC/REST; <paramref name="requests"/> may span multiple namespaces (a chunk's column
    /// mappings can reference different namespaces), all resolved in one round trip regardless.
    /// </summary>
    /// <returns>
    /// One entry per distinct (Namespace.Name, OriginalValue) pair in <paramref name="requests"/>
    /// - duplicates within <paramref name="requests"/> collapse onto the same entry rather than
    /// being generated/upserted twice.
    /// </returns>
    /// <exception cref="ArgumentException">Any request's original value is blank.</exception>
    /// <exception cref="OriginalValueValidationException">
    /// Any request's original value does not match its namespace's
    /// <see cref="Namespace.OriginalValueValidationRegex"/>.
    /// </exception>
    Task<
        IReadOnlyDictionary<(string Namespace, string OriginalValue), Pseudonym>
    > CreateTrustedBatchAsync(
        IReadOnlyList<(Namespace Namespace, string OriginalValue)> requests,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists pseudonyms in a namespace, keyset-paginated. Deliberately returns
    /// <see cref="PseudonymSummaryDto"/> rather than a type carrying the original value - this
    /// is the bulk projection exposed to external callers (gRPC/REST), and the original value
    /// must never cross into it. <see cref="ReverseLookupAsync"/> is the only way an external
    /// caller can see an original value, one record at a time. The Blazor pseudonym list page
    /// uses <see cref="SearchAsync"/> instead. Requires read access to the namespace.
    /// </summary>
    Task<PseudonymPageDto> ListAsync(
        string namespaceName,
        int pageSize,
        string? pageToken,
        bool includeTotalSize,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Offset-paginated, optionally search-filtered listing for the Blazor pseudonym list page -
    /// unlike <see cref="ListAsync"/>'s keyset pagination, this always returns a total count so
    /// the grid can render "page X of Y" and jump directly to a page. <paramref name="searchText"/>
    /// is matched as a case-insensitive substring against the pseudonym value, and - only when
    /// <paramref name="user"/> already has reverse-lookup access to the namespace - the original
    /// value too; null or blank returns every row. Each returned item's
    /// <see cref="PseudonymSearchItemDto.OriginalValue"/> is likewise populated only when
    /// <paramref name="user"/> has reverse-lookup access, null otherwise - the same gate
    /// <see cref="ReverseLookupAsync"/> uses, just applied per page instead of one pseudonym at a
    /// time, so the list page can show original values inline without a per-row reveal button.
    /// Requires read access to the namespace.
    /// </summary>
    Task<PseudonymSearchPageDto> SearchAsync(
        string namespaceName,
        string? searchText,
        int skip,
        int take,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Reveals the original value for a single pseudonym. This is a distinct, more tightly-gated
    /// action than <see cref="ListAsync"/> (requires reverse-lookup access, not just read access).
    /// </summary>
    Task<Pseudonym?> ReverseLookupAsync(
        string namespaceName,
        string pseudonymValue,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Same as <see cref="ReverseLookupAsync"/> but skips the per-call permission check - only
    /// for the CSV job runner, which already verified reverse-lookup access to every namespace a
    /// de-pseudonymization job's column mappings reference up front, at job creation time (see
    /// <see cref="IPseudonymizationJobAppService.CreateJobAsync"/>). Same reasoning as
    /// <see cref="CreateTrustedAsync(string, string, CancellationToken)"/> - the runner has no
    /// caller <see cref="ClaimsPrincipal"/> to re-check against.
    /// </summary>
    Task<Pseudonym?> ReverseLookupTrustedAsync(
        string namespaceName,
        string pseudonymValue,
        CancellationToken cancellationToken
    );
}

/// <summary>Pseudonym projection safe for bulk/list display - no original value.</summary>
public record PseudonymSummaryDto(
    string NamespaceName,
    string PseudonymValue,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt
);

public record PseudonymPageDto(
    IReadOnlyList<PseudonymSummaryDto> Items,
    string? NextPageToken,
    long? TotalSize
);

/// <summary>
/// Pseudonym projection for <see cref="IPseudonymAppService.SearchAsync"/>. Unlike
/// <see cref="PseudonymSummaryDto"/>, this carries the original value - but only when the
/// requesting user has reverse-lookup access; it's null otherwise. Never null/non-null on a
/// per-row basis within one page - it's a namespace-wide permission, so every item in a given
/// <see cref="PseudonymSearchPageDto"/> has it set the same way.
/// </summary>
public record PseudonymSearchItemDto(
    string NamespaceName,
    string PseudonymValue,
    string? OriginalValue,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt
);

public record PseudonymSearchPageDto(IReadOnlyList<PseudonymSearchItemDto> Items, long TotalCount);

public class NamespaceNotFoundException(string namespaceName)
    : Exception($"The requested pseudonym namespace '{namespaceName}' does not exist.")
{
    public string NamespaceName { get; } = namespaceName;
}

/// <summary>Thrown when the upsert retry loop in <see cref="Data.IPseudonymRepository.CreateIfNotExist"/> is exhausted.</summary>
public class PseudonymUpsertFailedException(string namespaceName)
    : Exception(
        $"Failed to upsert the pseudonym for namespace '{namespaceName}' after several retries."
    )
{
    public string NamespaceName { get; } = namespaceName;
}

/// <summary>
/// Thrown when an original value fails a namespace's <see cref="Namespace.OriginalValueValidationRegex"/>
/// check, before any pseudonym is generated for it.
/// </summary>
public class OriginalValueValidationException(string namespaceName, string pattern)
    : Exception(
        $"The original value does not match the required pattern '{pattern}' for namespace '{namespaceName}'."
    )
{
    public string NamespaceName { get; } = namespaceName;
    public string Pattern { get; } = pattern;
}

/// <summary>
/// Thrown when a pseudonym Create call asks for more than one pseudonym (<c>count &gt; 1</c>)
/// against a namespace whose <see cref="Namespace.AllowsMultiplePseudonyms"/> is false.
/// </summary>
public class MultiplePseudonymsNotAllowedException(string namespaceName)
    : Exception(
        $"Namespace '{namespaceName}' does not allow storing multiple pseudonyms per original value."
    )
{
    public string NamespaceName { get; } = namespaceName;
}
