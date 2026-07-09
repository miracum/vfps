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
    /// Creates (or fetches the existing) pseudonym for <paramref name="originalValue"/> in
    /// <paramref name="namespaceName"/>. Requires write access to the namespace. Shared by the
    /// gRPC adapter and <see cref="CsvProcessing.CsvPseudonymizationJobRunner"/>, so both paths
    /// get identical generation logic and per-namespace write-access enforcement.
    /// </summary>
    Task<Pseudonym> CreateAsync(
        string namespaceName,
        string originalValue,
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
    Task<Pseudonym> CreateTrustedAsync(
        string namespaceName,
        string originalValue,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists pseudonyms in a namespace, keyset-paginated. Deliberately returns
    /// <see cref="PseudonymSummaryDto"/> rather than a type carrying the original value - this
    /// is the read path that a UI renders in bulk, and the original value must never cross into
    /// it. <see cref="ReverseLookupAsync"/> is the only way to see an original value, one record
    /// at a time. Requires read access to the namespace.
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
    /// Reveals the original value for a single pseudonym. This is a distinct, more tightly-gated
    /// action than <see cref="ListAsync"/> (requires reverse-lookup access, not just read access)
    /// and every call is audit-logged.
    /// </summary>
    Task<Pseudonym?> ReverseLookupAsync(
        string namespaceName,
        string pseudonymValue,
        ClaimsPrincipal user,
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
