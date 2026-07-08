namespace Vfps.AppServices;

/// <summary>
/// Pseudonym-listing operations shared by the gRPC adapter (<see cref="Services.PseudonymService"/>)
/// and Blazor Server components. Only covers listing for now (Create/reverse-lookup aren't part
/// of the read-only browsing phase this was introduced for) - see PseudonymService.cs for those,
/// still handled directly there.
/// </summary>
public interface IPseudonymAppService
{
    /// <summary>
    /// Lists pseudonyms in a namespace, keyset-paginated. Deliberately returns
    /// <see cref="PseudonymSummaryDto"/> rather than a type carrying the original value - this
    /// is the read path that a UI renders in bulk, and the original value must never cross into
    /// it. Reverse lookup is the only way to see an original value, one record at a time, and is
    /// a separate, more tightly-gated action.
    /// </summary>
    Task<PseudonymPageDto> ListAsync(
        string namespaceName,
        int pageSize,
        string? pageToken,
        bool includeTotalSize,
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
