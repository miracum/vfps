using System.Security.Claims;
using System.Text.RegularExpressions;
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
    public async Task<IReadOnlyList<Data.Models.Pseudonym>> CreateAsync(
        string namespaceName,
        string originalValue,
        long count,
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

        var @namespace =
            await namespaceRepository.FindAsync(namespaceName, cancellationToken)
            ?? throw new NamespaceNotFoundException(namespaceName);

        return await CreateTrustedAsync(@namespace, originalValue, count, cancellationToken);
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
        var created = await CreateTrustedAsync(@namespace, originalValue, 1, cancellationToken);
        return created[0];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Data.Models.Pseudonym>> CreateTrustedAsync(
        Data.Models.Namespace @namespace,
        string originalValue,
        long count,
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

        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "count must be at least 1.");
        }

        if (count > 1 && !@namespace.AllowsMultiplePseudonyms)
        {
            throw new MultiplePseudonymsNotAllowedException(@namespace.Name);
        }

        ValidateOriginalValue(@namespace, originalValue);

        // A fresh, pooled DbContext per call rather than the scoped pseudonymRepository field -
        // this method is what the CSV job runner calls, many times concurrently, within a
        // single Hangfire job's DI scope. DbContext instances aren't safe for concurrent use, so
        // the scoped one shared across that whole job would throw if used this way.
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var repository = new PseudonymRepository(context);

        // Grow-to-N idempotency: a count at or below what's already stored is always a no-op -
        // existing pseudonyms are never regenerated or truncated, only ever added to.
        var existing = await repository.FindAllByOriginalValueAsync(
            @namespace.Name,
            originalValue,
            cancellationToken
        );
        if (existing.Count >= count)
        {
            return existing;
        }

        var knownPseudonymValues = new HashSet<string>(
            existing.Select(p => p.PseudonymValue),
            StringComparer.Ordinal
        );
        var newSequenceCandidates = new List<Data.Models.Pseudonym>((int)(count - existing.Count));
        for (var sequenceNumber = existing.Count; sequenceNumber < count; sequenceNumber++)
        {
            newSequenceCandidates.Add(
                new Data.Models.Pseudonym
                {
                    NamespaceName = @namespace.Name,
                    OriginalValue = originalValue,
                    PseudonymValue = GenerateUniquePseudonymValue(@namespace, knownPseudonymValues),
                    SequenceNumber = sequenceNumber,
                }
            );
        }

        return await repository.CreateSetIfNotExistAsync(newSequenceCandidates, cancellationToken);
    }

    // Bounded retries against an in-batch collision - astronomically unlikely for any registered
    // (all non-deterministic) generator at a realistic length, but a real possibility now that
    // several values are generated for the same original value in one call, rather than each
    // value being generated independently across the whole table.
    private const int MaxGenerationAttempts = 5;

    private string GenerateUniquePseudonymValue(
        Data.Models.Namespace @namespace,
        HashSet<string> knownPseudonymValues
    )
    {
        for (var attempt = 0; attempt < MaxGenerationAttempts; attempt++)
        {
            string pseudonymValue;
            using (var activity = Program.ActivitySource.StartActivity("GeneratePseudonym"))
            {
                activity?.SetTag("Method", @namespace.PseudonymGenerationMethod.ToString());
                pseudonymValue = methodsLookup.Generate(
                    @namespace.PseudonymGenerationMethod,
                    @namespace.PseudonymLength
                );
            }
            pseudonymValue =
                @namespace.PseudonymPrefix + pseudonymValue + @namespace.PseudonymSuffix;

            if (knownPseudonymValues.Add(pseudonymValue))
            {
                return pseudonymValue;
            }
        }

        throw new PseudonymUpsertFailedException(@namespace.Name);
    }

    /// <inheritdoc/>
    public async Task<
        IReadOnlyDictionary<(string Namespace, string OriginalValue), Data.Models.Pseudonym>
    > CreateTrustedBatchAsync(
        IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)> requests,
        CancellationToken cancellationToken
    )
    {
        if (requests.Count == 0)
        {
            return new Dictionary<(string, string), Data.Models.Pseudonym>();
        }

        // Dedupe up front - a CSV chunk routinely repeats the same value (e.g. a patient ID
        // column), and there's no reason to generate a candidate pseudonym or send a duplicate
        // row over the wire more than once per chunk. The first candidate generated for a given
        // key wins; duplicates just look it up once resolved below.
        var distinctByKey =
            new Dictionary<(string Namespace, string OriginalValue), Data.Models.Pseudonym>();
        foreach (var (@namespace, originalValue) in requests)
        {
            if (string.IsNullOrWhiteSpace(originalValue))
            {
                throw new ArgumentException(
                    "The original value must not be blank.",
                    nameof(requests)
                );
            }

            var key = (@namespace.Name, originalValue);
            if (distinctByKey.ContainsKey(key))
            {
                continue;
            }

            ValidateOriginalValue(@namespace, originalValue);

            string pseudonymValue;
            using (var activity = Program.ActivitySource.StartActivity("GeneratePseudonym"))
            {
                activity?.SetTag("Method", @namespace.PseudonymGenerationMethod.ToString());
                pseudonymValue = methodsLookup.Generate(
                    @namespace.PseudonymGenerationMethod,
                    @namespace.PseudonymLength
                );
            }
            pseudonymValue =
                @namespace.PseudonymPrefix + pseudonymValue + @namespace.PseudonymSuffix;

            // SequenceNumber left at its default (0) - this batch path always targets the first
            // pseudonym for an original value, same as CreateTrustedAsync's single-value overload.
            // For a multi-psn namespace that already has additional (sequence > 0) pseudonyms
            // stored via the dedicated count-aware create path, those are left untouched; this
            // path only ever creates/reads sequence 0.
            distinctByKey[key] = new Data.Models.Pseudonym
            {
                NamespaceName = @namespace.Name,
                OriginalValue = originalValue,
                PseudonymValue = pseudonymValue,
            };
        }

        // Same fresh, pooled DbContext reasoning as CreateTrustedAsync(Namespace, ...) above.
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var upserted = await new PseudonymRepository(context).CreateIfNotExistBatchAsync(
            [.. distinctByKey.Values],
            cancellationToken
        );

        var result = new Dictionary<(string, string), Data.Models.Pseudonym>();
        foreach (var pseudonym in upserted)
        {
            result[(pseudonym.NamespaceName, pseudonym.OriginalValue)] = pseudonym;
        }

        return result;
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
                new PseudonymPageCursor(last.CreatedAt, last.OriginalValue, last.SequenceNumber)
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
    public async Task<PseudonymSearchPageDto> SearchAsync(
        string namespaceName,
        string? searchText,
        int skip,
        int take,
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

        var canRevealOriginalValues = permissionChecker.HasReverseLookupAccess(user, namespaceName);
        var effectiveTake = take <= 0 ? DefaultPageSize : take;

        var (pseudonyms, totalCount) = await pseudonymRepository.SearchByNamespaceAsync(
            namespaceName,
            searchText,
            canRevealOriginalValues,
            Math.Max(skip, 0),
            effectiveTake,
            cancellationToken
        );

        var items = pseudonyms
            .Select(p => new PseudonymSearchItemDto(
                p.NamespaceName,
                p.PseudonymValue,
                canRevealOriginalValues ? p.OriginalValue : null,
                p.CreatedAt,
                p.LastUpdatedAt
            ))
            .ToList();

        return new PseudonymSearchPageDto(items, totalCount);
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

    // A short, fixed timeout guards against a catastrophically backtracking pattern turning a
    // single pseudonym request into a denial of service - the pattern is admin-supplied at
    // namespace creation, not attacker-controlled, but this is cheap insurance regardless.
    private static readonly TimeSpan ValidationRegexTimeout = TimeSpan.FromMilliseconds(500);

    private static void ValidateOriginalValue(
        Data.Models.Namespace @namespace,
        string originalValue
    )
    {
        var pattern = @namespace.OriginalValueValidationRegex;
        if (string.IsNullOrEmpty(pattern))
        {
            return;
        }

        if (!Regex.IsMatch(originalValue, pattern, RegexOptions.None, ValidationRegexTimeout))
        {
            throw new OriginalValueValidationException(@namespace.Name, pattern);
        }
    }

    private static PseudonymPageCursor? DecodeCursor(string? pageToken)
    {
        if (string.IsNullOrEmpty(pageToken))
        {
            return null;
        }

        var token = new PseudonymListPaginationToken();
        token.MergeFrom(WebEncoders.Base64UrlDecode(pageToken));

        return new PseudonymPageCursor(
            token.CreatedAt.ToDateTimeOffset(),
            token.OriginalValue,
            token.SequenceNumber
        );
    }

    private static string EncodeCursor(PseudonymPageCursor cursor)
    {
        var token = new PseudonymListPaginationToken
        {
            CreatedAt = Timestamp.FromDateTimeOffset(cursor.CreatedAt),
            OriginalValue = cursor.OriginalValue,
            SequenceNumber = cursor.SequenceNumber,
        };

        return WebEncoders.Base64UrlEncode(token.ToByteArray());
    }
}
