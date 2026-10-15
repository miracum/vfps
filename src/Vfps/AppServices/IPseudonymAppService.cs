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
    /// Must be between 1 and <see cref="PseudonymAppService.MaxCount"/>; anything above 1 requires
    /// the namespace's <see cref="Namespace.AllowsMultiplePseudonyms"/> to be set. If fewer than <paramref name="count"/>
    /// already exist, exactly the missing ones are generated and added (existing ones are never
    /// regenerated); if this many or more already exist, the existing set is returned unchanged -
    /// a given original value's stored set only ever grows, so a repeat call with the same or a
    /// smaller <paramref name="count"/> is always a no-op.
    /// </param>
    /// <param name="user">The caller, checked for write access to <paramref name="namespaceName"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every pseudonym stored for <paramref name="originalValue"/> after this call, ordered by sequence number.</returns>
    /// <exception cref="ArgumentException"><paramref name="originalValue"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> is less than 1 or greater than <see cref="PseudonymAppService.MaxCount"/>.
    /// </exception>
    /// <exception cref="MultiplePseudonymsNotAllowedException">
    /// <paramref name="count"/> is greater than 1 but the namespace doesn't allow multiple pseudonyms.
    /// </exception>
    /// <exception cref="OriginalValueValidationException">
    /// <paramref name="originalValue"/> does not match the namespace's
    /// <see cref="Namespace.OriginalValueValidationRegex"/> and has no pseudonym stored yet.
    /// </exception>
    Task<IReadOnlyList<Pseudonym>> CreateAsync(
        string namespaceName,
        string originalValue,
        long count,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Stores one additional, newly generated pseudonym for <paramref name="originalValue"/> in
    /// <paramref name="namespaceName"/>, at the sequence number after the highest already stored
    /// for it (0 if none is). Requires write access to the namespace, the same as
    /// <see cref="CreateAsync"/>.
    ///
    /// The non-idempotent counterpart to <see cref="CreateAsync"/>'s grow-to-N: every successful
    /// call adds a pseudonym, which is why it is a separate operation rather than a mode of
    /// <see cref="CreateAsync"/> - a caller retrying a create must never end up appending.
    ///
    /// <paramref name="requestId"/> makes it safe to retry anyway (AIP-155): a client-chosen id
    /// identifying the request across retries, scoped to the namespace. A call carrying the id of
    /// an earlier successful call in the same namespace stores nothing and returns the pseudonym
    /// that call stored. Without one (null), every call appends.
    /// </summary>
    /// <returns>
    /// The pseudonym this call stored - or, for a retry, the one the original call stored.
    /// </returns>
    /// <exception cref="AppendRequestIdConflictException">
    /// <paramref name="requestId"/> was already used in this namespace by an Append for a
    /// different original value.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="originalValue"/> is blank.</exception>
    /// <exception cref="NamespaceNotFoundException">The namespace does not exist.</exception>
    /// <exception cref="Authorization.ForbiddenException">
    /// <paramref name="user"/> has no write access to it.
    /// </exception>
    /// <exception cref="MultiplePseudonymsNotAllowedException">
    /// The namespace doesn't allow multiple pseudonyms per original value.
    /// </exception>
    /// <exception cref="OriginalValueValidationException">
    /// <paramref name="originalValue"/> does not match the namespace's
    /// <see cref="Namespace.OriginalValueValidationRegex"/> and has no pseudonym stored yet.
    /// </exception>
    /// <exception cref="ParentPseudonymNotFoundException">
    /// The namespace validates against its parent and <paramref name="originalValue"/> isn't a
    /// pseudonym there.
    /// </exception>
    /// <exception cref="PseudonymUpsertFailedException">
    /// Concurrent appends for the same original value kept claiming the next sequence number
    /// first, past every retry.
    /// </exception>
    Task<Pseudonym> AppendAsync(
        string namespaceName,
        string originalValue,
        Guid? requestId,
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
    /// A value with nothing stored is validated against the namespace's
    /// <see cref="Namespace.OriginalValueValidationRegex"/>, so one that could never be stored is
    /// rejected as invalid rather than reported as absent. A stored value is returned even if it
    /// no longer matches: the pattern can be edited after creation, and only governs values new
    /// to the namespace.
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
    /// <paramref name="originalValue"/> does not match the namespace's validation pattern and has
    /// no pseudonym stored.
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
    /// <see cref="CreateTrustedBatchAsync"/> creates.
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
    /// <see cref="CreateAsync"/> once its permission check and namespace lookup are done - the
    /// core create/grow logic, for a caller that has already done both itself. See
    /// <see cref="CreateAsync"/> for the grow-to-N semantics.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="originalValue"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> is less than 1 or greater than <see cref="PseudonymAppService.MaxCount"/>.
    /// </exception>
    /// <exception cref="MultiplePseudonymsNotAllowedException">
    /// <paramref name="count"/> is greater than 1 but the namespace doesn't allow multiple pseudonyms.
    /// </exception>
    /// <exception cref="OriginalValueValidationException">
    /// <paramref name="originalValue"/> does not match the namespace's
    /// <see cref="Namespace.OriginalValueValidationRegex"/> and has no pseudonym stored yet.
    /// </exception>
    Task<IReadOnlyList<Pseudonym>> CreateTrustedAsync(
        Namespace @namespace,
        string originalValue,
        long count,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates (or returns the existing) first pseudonym for many values at once, in a single
    /// database round trip, without a per-call permission check - only for the CSV job runner,
    /// which already verified write access to every namespace a job's column mappings reference
    /// up front, at job creation time (see <see cref="IPseudonymizationJobAppService.CreateJobAsync"/>).
    /// The runner has no caller <see cref="ClaimsPrincipal"/> to re-check against - it runs later,
    /// in a Hangfire background thread, well after the request that created the job. Not exposed via
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
    /// <see cref="Namespace.OriginalValueValidationRegex"/> and has no pseudonym stored yet.
    /// </exception>
    Task<
        IReadOnlyDictionary<(string Namespace, string OriginalValue), Pseudonym>
    > CreateTrustedBatchAsync(
        IReadOnlyList<(Namespace Namespace, string OriginalValue)> requests,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Stores one pseudonym created outside vfps for <paramref name="originalValue"/>, verbatim,
    /// in a namespace using <see cref="Protos.PseudonymGenerationMethod.External"/> - the single-pair,
    /// permission-checked, fail-on-conflict counterpart to <see cref="ImportTrustedBatchAsync"/>,
    /// which it delegates to. Requires write access to the namespace, like
    /// <see cref="CreateAsync"/>.
    ///
    /// Idempotent for the exact same pair: importing what is already stored succeeds and returns
    /// the stored row. Nothing is ever overwritten.
    /// </summary>
    /// <returns>The stored pseudonym, including its original value.</returns>
    /// <exception cref="Authorization.ForbiddenException">The caller has no write access to the namespace.</exception>
    /// <exception cref="NamespaceNotFoundException">The namespace does not exist.</exception>
    /// <exception cref="NamespaceNotExternalException">The namespace doesn't use the External method.</exception>
    /// <exception cref="ArgumentException">The original or pseudonym value is blank.</exception>
    /// <exception cref="OriginalValueValidationException">
    /// The original value doesn't match the namespace's validation regex and has nothing stored yet.
    /// </exception>
    /// <exception cref="ParentPseudonymNotFoundException">
    /// The namespace requires its original values to exist in its parent and this one doesn't.
    /// </exception>
    /// <exception cref="PseudonymConflictException">
    /// The pair collides with what is stored: the original value already has a different
    /// pseudonym, or the pseudonym value already belongs to a different original value.
    /// </exception>
    Task<Pseudonym> ImportAsync(
        string namespaceName,
        string originalValue,
        string pseudonymValue,
        ClaimsPrincipal user,
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
    /// of these pseudonym values are already taken, and the batched upsert itself. Two more take
    /// and release the namespace's import lock around them (see
    /// <see cref="Data.IPseudonymRepository.AcquireImportLockAsync"/>), which serializes concurrent
    /// imports into the same namespace call by call.
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
    /// <see cref="IPseudonymizationJobAppService.CreateJobAsync"/>) - and resolves a whole chunk's
    /// worth of values in one round trip per distinct namespace instead of one per value. The
    /// reverse-lookup counterpart to
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
    /// <see cref="Namespace.OriginalValueValidationRegex"/> and isn't stored in it yet.
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

/// <summary>
/// Thrown when no pseudonym could be produced for a value: every attempt at a unique one collided
/// (including with one already stored for the same original value), a concurrent writer racing
/// the same key defeated every retry, or a value-dependent generator returned fewer pseudonyms
/// than it was asked for.
/// </summary>
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
/// Thrown when an externally created pseudonym is imported into a namespace that doesn't use
/// <see cref="Protos.PseudonymGenerationMethod.External"/>, where it would sit next to pseudonyms vfps
/// generated itself.
/// </summary>
public class NamespaceNotExternalException(string namespaceName)
    : Exception(
        $"Namespace '{namespaceName}' does not use the 'External' pseudonym generation method, "
            + "so pseudonyms can't be imported into it."
    )
{
    public string NamespaceName { get; } = namespaceName;
}

/// <summary>
/// Thrown when importing a pseudonym would contradict what a namespace already stores. The message
/// deliberately doesn't say which original value holds a conflicting pseudonym - that would be a
/// reverse lookup, which the write access an import needs doesn't grant.
/// </summary>
public class PseudonymConflictException(string namespaceName, PseudonymImportOutcome outcome)
    : Exception(
        outcome == PseudonymImportOutcome.PseudonymValueConflict
            ? $"The pseudonym value is already in use for a different original value in namespace '{namespaceName}'."
            : $"The original value already has a different pseudonym in namespace '{namespaceName}', "
                + "which doesn't allow multiple pseudonyms per original value."
    )
{
    public string NamespaceName { get; } = namespaceName;
    public PseudonymImportOutcome Outcome { get; } = outcome;
}

/// <summary>
/// Thrown when an Append reuses a request id that an earlier Append in the same namespace used
/// for a different original value. Deliberately says nothing about which value that was or what
/// was stored for it: answering with the earlier call's pseudonym would hand out the pseudonym of
/// a value the caller never named.
/// </summary>
public class AppendRequestIdConflictException(string namespaceName, Guid requestId)
    : Exception(
        $"The request id '{requestId}' was already used in namespace '{namespaceName}' for a "
            + "different request. Use a new request id for every distinct Append, and the same "
            + "one only to retry it."
    )
{
    public string NamespaceName { get; } = namespaceName;
    public Guid RequestId { get; } = requestId;
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
