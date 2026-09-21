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
    /// Resolves the pseudonym(s) already stored for <paramref name="originalValue"/> in
    /// <paramref name="namespaceName"/>, creating nothing. The lookup-only counterpart to
    /// <see cref="CreateAsync"/>, for a caller whose file or request is supposed to contain only
    /// values the namespace already knows - where minting a pseudonym for one it doesn't would
    /// quietly record a subject that was never meant to be in it.
    ///
    /// Requires write access, the same as <see cref="CreateAsync"/>: this is the same forward
    /// mapping, and write access already lets a caller obtain it for any value it can supply. It
    /// is not quite a subset, though, and the difference is worth knowing - a create call cannot
    /// tell you whether the pseudonym it handed back already existed, and this one can. That makes
    /// it an existence oracle over the namespace's original values ("was this subject ever in this
    /// cohort?"), which is why it is gated on write rather than on read access.
    ///
    /// <paramref name="originalValue"/> is validated against the namespace's
    /// <see cref="Namespace.OriginalValueValidationRegex"/> before the lookup, so an invalid value
    /// is rejected identically whether or not a pseudonym happens to exist for it - the same
    /// reason <see cref="CreateAsync"/> validates ahead of its own grow-to-N short circuit.
    /// </summary>
    /// <returns>
    /// Every pseudonym stored for <paramref name="originalValue"/>, ordered by sequence number -
    /// empty when the namespace holds none, which is the caller's "not found".
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="originalValue"/> is blank.</exception>
    /// <exception cref="NamespaceNotFoundException">The namespace does not exist.</exception>
    /// <exception cref="Authorization.ForbiddenException">
    /// <paramref name="user"/> has no write access to it.
    /// </exception>
    /// <exception cref="OriginalValueValidationException">
    /// <paramref name="originalValue"/> does not match the namespace's validation pattern.
    /// </exception>
    Task<IReadOnlyList<Pseudonym>> ResolveAsync(
        string namespaceName,
        string originalValue,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// The lookup-only counterpart to <see cref="CreateTrustedBatchAsync"/>, with the same trust
    /// boundary and the same batching rationale - one round trip per distinct namespace rather
    /// than one per value. Backs
    /// <see cref="PseudonymizeMode.FailIfMissing"/> and
    /// <see cref="PseudonymizeMode.KeepIfMissing"/> CSV jobs.
    ///
    /// Returns the same shape as <see cref="ReverseLookupTrustedBatchAsync"/>, deliberately: a
    /// value with no stored pseudonym is simply absent from the dictionary rather than mapping to
    /// null, so the two lookup-only paths and the create path are interchangeable behind one
    /// resolver signature in <see cref="CsvProcessing.CsvColumnTransformer"/>. In a multi-psn
    /// namespace the entry is the first (sequence number 0) pseudonym, matching what
    /// <see cref="CreateTrustedAsync(Namespace, string, CancellationToken)"/> returns.
    ///
    /// No original-value validation here, unlike <see cref="ResolveAsync"/>: nothing is written, a
    /// value that could never have been stored simply has nothing to find, and running a regex per
    /// field per row to reach that same answer would be the dominant per-row cost.
    /// </summary>
    Task<
        IReadOnlyDictionary<(string Namespace, string OriginalValue), Pseudonym>
    > ResolveTrustedBatchAsync(
        IReadOnlyList<(Namespace Namespace, string OriginalValue)> requests,
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
    /// Stores already-known original/pseudonym pairs verbatim in <paramref name="namespace"/>,
    /// generating nothing - the bulk-import counterpart to
    /// <see cref="CreateTrustedBatchAsync"/>, and the only way a pseudonym value chosen outside
    /// vfps ever enters the store. Same trust boundary as the other <c>Trusted</c> methods: write
    /// access to the namespace is checked once at job creation (see
    /// <see cref="IPseudonymizationJobAppService.CreateJobAsync"/>), not per call.
    ///
    /// Nothing here throws over a row that merely collides with what's already stored - every
    /// entry gets a <see cref="PseudonymImportOutcome"/> instead, so one bad row in a million-row
    /// file is reported rather than failing the whole import. Existing pseudonyms are never
    /// overwritten or deleted: an original value that already has one keeps it (reported as
    /// <see cref="PseudonymImportOutcome.OriginalValueConflict"/>), unless the namespace allows
    /// multiple pseudonyms, in which case the imported value is appended at the next free sequence
    /// number - the same grow-only semantics
    /// <see cref="CreateTrustedAsync(Namespace, string, long, CancellationToken)"/>
    /// has.
    ///
    /// Costs three round trips per call regardless of <paramref name="entries"/>'s size (plus one
    /// per validating parent namespace): what's already stored for these original values, which
    /// of these pseudonym values are already taken, and the batched upsert itself.
    /// </summary>
    /// <returns>
    /// One result per entry in <paramref name="entries"/>, in the same order - including for
    /// entries that collided with an earlier one in the same call.
    /// </returns>
    /// <exception cref="ArgumentException">Any entry's original or pseudonym value is blank.</exception>
    Task<IReadOnlyList<PseudonymImportResult>> ImportTrustedBatchAsync(
        Namespace @namespace,
        IReadOnlyList<PseudonymImportEntry> entries,
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

    /// <summary>
    /// Same as <see cref="ReverseLookupTrustedAsync"/> - including its trust boundary - but
    /// resolves a whole chunk's worth of values in one round trip per distinct namespace instead
    /// of one per value. The reverse-lookup counterpart to
    /// <see cref="CreateTrustedBatchAsync"/>, and the reason a de-pseudonymizing CSV job no
    /// longer costs a database call per row.
    ///
    /// A value with no matching pseudonym is simply absent from the returned dictionary rather
    /// than mapping to null: callers leave such a field unchanged (see
    /// <see cref="CsvProcessing.CsvColumnTransformer"/>), and an absent key expresses that
    /// without a nullable value in the dictionary.
    /// </summary>
    Task<
        IReadOnlyDictionary<(string Namespace, string PseudonymValue), Pseudonym>
    > ReverseLookupTrustedBatchAsync(
        IReadOnlyList<(Namespace Namespace, string PseudonymValue)> requests,
        CancellationToken cancellationToken
    );
}

/// <summary>One already-known pair to store via <see cref="IPseudonymAppService.ImportTrustedBatchAsync"/>.</summary>
public record PseudonymImportEntry(string OriginalValue, string PseudonymValue);

/// <summary>
/// What happened to one <see cref="PseudonymImportEntry"/>. Only
/// <see cref="Imported"/> wrote anything; every other member means the store was left exactly as
/// it was. Surfaced per row in a namespace import job's output report (see
/// <see cref="CsvProcessing.CsvNamespaceImporter"/>), so the names double as that report's
/// <c>status</c> column values and shouldn't be renamed lightly.
/// </summary>
public enum PseudonymImportOutcome
{
    /// <summary>Stored as given.</summary>
    Imported,

    /// <summary>This exact pair was already stored - a no-op, which makes re-running an import idempotent.</summary>
    AlreadyPresent,

    /// <summary>
    /// The original value already has a different pseudonym, and the namespace doesn't allow more
    /// than one per original value. The stored one is kept and deliberately not reported back
    /// here: the report is written to object storage, and the import path is gated on write
    /// access, which is not enough to be shown a mapping the caller didn't already have.
    /// </summary>
    OriginalValueConflict,

    /// <summary>
    /// The pseudonym value is already in use in this namespace for a different original value.
    /// Storing it anyway would make the reverse lookup for that pseudonym ambiguous, so the row
    /// is skipped. Which original value holds it is never reported back - that would be a reverse
    /// lookup, which write access doesn't grant.
    /// </summary>
    PseudonymValueConflict,

    /// <summary>
    /// The original value doesn't match the namespace's
    /// <see cref="Namespace.OriginalValueValidationRegex"/>.
    /// </summary>
    InvalidOriginalValue,

    /// <summary>
    /// The namespace requires its original values to already exist as pseudonyms in its parent
    /// (<see cref="Namespace.ParentValidationMode"/>) and this one doesn't.
    /// </summary>
    ParentValueMissing,
}

/// <summary>One entry's fate - see <see cref="IPseudonymAppService.ImportTrustedBatchAsync"/>.</summary>
public record PseudonymImportResult(PseudonymImportEntry Entry, PseudonymImportOutcome Outcome);

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
/// Thrown when a namespace requires its original values to already exist as pseudonyms in its
/// parent namespace (<see cref="Namespace.ParentValidationMode"/>) and a given value doesn't.
/// Like the other validation exceptions here, the message deliberately doesn't echo the rejected
/// value back to the caller.
/// </summary>
public class ParentPseudonymNotFoundException(string namespaceName, string parentNamespaceName)
    : Exception(
        $"The original value does not exist as a pseudonym in the parent namespace "
            + $"'{parentNamespaceName}' required by namespace '{namespaceName}'."
    )
{
    public string NamespaceName { get; } = namespaceName;
    public string ParentNamespaceName { get; } = parentNamespaceName;
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
