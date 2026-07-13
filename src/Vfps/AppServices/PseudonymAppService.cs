using System.Security.Claims;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Vfps.Authorization;
using Vfps.Data;
using Vfps.Protos;
using Vfps.PseudonymGenerators;

namespace Vfps.AppServices;

/// <inheritdoc cref="IPseudonymAppService"/>
public class PseudonymAppService(
    INamespaceRepository namespaceRepository,
    IPseudonymRepository pseudonymRepository,
    INamespacePermissionChecker permissionChecker,
    PseudonymizationMethodsLookup methodsLookup,
    IDbContextFactory<PseudonymContext> contextFactory
) : IPseudonymAppService
{
    private const int DefaultPageSize = 25;

    /// <inheritdoc/>
    public async Task<Data.Models.Pseudonym> CreateAsync(
        string namespaceName,
        string originalValue,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (!permissionChecker.HasWriteAccess(user, namespaceName))
        {
            throw new ForbiddenException(
                $"Write access to namespace '{namespaceName}' is required."
            );
        }

        return await CreateTrustedAsync(namespaceName, originalValue, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Data.Models.Pseudonym> CreateTrustedAsync(
        string namespaceName,
        string originalValue,
        CancellationToken cancellationToken
    )
    {
        var @namespace =
            await namespaceRepository.FindAsync(namespaceName, cancellationToken)
            ?? throw new NamespaceNotFoundException(namespaceName);

        return await CreateTrustedAsync(@namespace, originalValue, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Data.Models.Pseudonym> CreateTrustedAsync(
        Data.Models.Namespace @namespace,
        string originalValue,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(originalValue))
        {
            throw new ArgumentException(
                "The original value must not be blank.",
                nameof(originalValue)
            );
        }

        string pseudonymValue;
        using (var activity = Program.ActivitySource.StartActivity("GeneratePseudonym"))
        {
            activity?.SetTag("Method", @namespace.PseudonymGenerationMethod.ToString());
            pseudonymValue = methodsLookup.Generate(
                @namespace.PseudonymGenerationMethod,
                originalValue,
                @namespace.PseudonymLength
            );
        }
        pseudonymValue = @namespace.PseudonymPrefix + pseudonymValue + @namespace.PseudonymSuffix;

        var pseudonym = new Data.Models.Pseudonym
        {
            NamespaceName = @namespace.Name,
            OriginalValue = originalValue,
            PseudonymValue = pseudonymValue,
        };

        // A fresh, pooled DbContext per call rather than the scoped pseudonymRepository above -
        // this overload is what the CSV job runner calls, many times concurrently, within a
        // single Hangfire job's DI scope. DbContext instances aren't safe for concurrent use, so
        // the scoped one shared across that whole job would throw if used this way.
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var upserted = await new PseudonymRepository(context).CreateIfNotExist(pseudonym);
        return upserted ?? throw new PseudonymUpsertFailedException(@namespace.Name);
    }

    /// <inheritdoc/>
    public async Task<PseudonymPageDto> ListAsync(
        string namespaceName,
        int pageSize,
        string? pageToken,
        bool includeTotalSize,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var _ =
            await namespaceRepository.FindAsync(namespaceName, cancellationToken)
            ?? throw new NamespaceNotFoundException(namespaceName);
        if (!permissionChecker.HasReadAccess(user, namespaceName))
        {
            throw new ForbiddenException(
                $"Read access to namespace '{namespaceName}' is required."
            );
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

    /// <inheritdoc/>
    public async Task<Data.Models.Pseudonym?> ReverseLookupAsync(
        string namespaceName,
        string pseudonymValue,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (!permissionChecker.HasReverseLookupAccess(user, namespaceName))
        {
            throw new ForbiddenException(
                $"Reverse-lookup access to namespace '{namespaceName}' is required."
            );
        }

        return await pseudonymRepository.FindByPseudonymValueAsync(
            namespaceName,
            pseudonymValue,
            cancellationToken
        );
    }

    /// <inheritdoc/>
    public async Task<Data.Models.Pseudonym?> ReverseLookupTrustedAsync(
        string namespaceName,
        string pseudonymValue,
        CancellationToken cancellationToken
    )
    {
        // Same reasoning as CreateTrustedAsync(Namespace, ...) above - called many times
        // concurrently by the CSV job runner within a single Hangfire job's DI scope, so this
        // needs its own fresh, pooled DbContext rather than the shared scoped one.
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await new PseudonymRepository(context).FindByPseudonymValueAsync(
            namespaceName,
            pseudonymValue,
            cancellationToken
        );
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
