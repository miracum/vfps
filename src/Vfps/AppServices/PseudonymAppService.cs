using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.WebUtilities;
using Vfps.Data;
using Vfps.Protos;

namespace Vfps.AppServices;

/// <summary>
/// Implements the UI-facing pseudonym-listing contract on top of <see cref="IPseudonymRepository"/>.
/// Used directly by Blazor Server components (in-process) and by <see cref="Services.PseudonymService"/>
/// (the gRPC adapter).
/// </summary>
public class PseudonymAppService(
    INamespaceRepository namespaceRepository,
    IPseudonymRepository pseudonymRepository
) : IPseudonymAppService
{
    private const int DefaultPageSize = 25;

    /// <inheritdoc/>
    public async Task<PseudonymPageDto> ListAsync(
        string namespaceName,
        int pageSize,
        string? pageToken,
        bool includeTotalSize,
        CancellationToken cancellationToken
    )
    {
        var @namespace = await namespaceRepository.FindAsync(namespaceName, cancellationToken);
        if (@namespace is null)
        {
            throw new NamespaceNotFoundException(namespaceName);
        }

        var effectivePageSize = pageSize <= 0 ? DefaultPageSize : pageSize;
        var cursor = DecodeCursor(pageToken);

        var pseudonyms = await pseudonymRepository.ListByNamespaceAsync(
            namespaceName,
            cursor,
            effectivePageSize,
            cancellationToken
        );

        // Same "did we get a full page" heuristic as before this rewrite: if fewer than a full
        // page came back there's no next page. Imprecise only when the total count is an exact
        // multiple of the page size (one extra empty-result round trip) - an existing, accepted
        // trade-off, not a new one.
        string? nextPageToken = null;
        if (pseudonyms.Count == effectivePageSize)
        {
            var last = pseudonyms[^1];
            nextPageToken = EncodeCursor(
                new PseudonymPageCursor(last.CreatedAt, last.OriginalValue)
            );
        }

        long? totalSize = includeTotalSize
            ? await pseudonymRepository.CountByNamespaceAsync(namespaceName, cancellationToken)
            : null;

        var items = pseudonyms
            .Select(p => new PseudonymSummaryDto(
                p.NamespaceName,
                p.PseudonymValue,
                p.CreatedAt,
                p.LastUpdatedAt
            ))
            .ToList();

        return new PseudonymPageDto(items, nextPageToken, totalSize);
    }

    private static PseudonymPageCursor? DecodeCursor(string? pageToken)
    {
        if (string.IsNullOrEmpty(pageToken))
        {
            return null;
        }

        var token = new PseudonymListPaginationToken();
        token.MergeFrom(WebEncoders.Base64UrlDecode(pageToken));

        return new PseudonymPageCursor(token.CreatedAt.ToDateTimeOffset(), token.OriginalValue);
    }

    private static string EncodeCursor(PseudonymPageCursor cursor)
    {
        var token = new PseudonymListPaginationToken
        {
            CreatedAt = Timestamp.FromDateTimeOffset(cursor.CreatedAt),
            OriginalValue = cursor.OriginalValue,
        };

        return WebEncoders.Base64UrlEncode(token.ToByteArray());
    }
}
