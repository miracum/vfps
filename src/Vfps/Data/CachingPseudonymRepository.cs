using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Vfps.Config;
using Vfps.Data.Models;

namespace Vfps.Data;

public class CachingPseudonymRepository(
    IDbContextFactory<PseudonymContext> contextFactory,
    IMemoryCache memoryCache,
    CacheConfig cacheConfig
) : IPseudonymRepository
{
    private IMemoryCache MemoryCache { get; } = memoryCache;
    private CacheConfig CacheConfig { get; } = cacheConfig;
    private PseudonymRepository Repository { get; } = new(contextFactory);

    /// <inheritdoc/>
    public async Task<Pseudonym?> FindFirstByOriginalValueAsync(
        Namespace @namespace,
        string originalValue,
        CancellationToken cancellationToken
    )
    {
        // CreatedAt is part of the key because deleting a namespace takes its pseudonyms with it:
        // one re-created under the same name must not be served the old one's entries.
        var cacheKey = new FirstPseudonymKey(@namespace.Name, @namespace.CreatedAt, originalValue);

        if (MemoryCache.TryGetValue(cacheKey, out Pseudonym? cached))
        {
            return cached;
        }

        var found = await Repository.FindFirstByOriginalValueAsync(
            @namespace,
            originalValue,
            cancellationToken
        );

        // Only a hit is cached, never a miss: a value looked up on the create path is usually
        // about to be stored, and a cached miss would hide it until the entry expired.
        if (found is not null)
        {
            MemoryCache.Set(
                cacheKey,
                found,
                new MemoryCacheEntryOptions()
                    .SetSize(1)
                    .SetAbsoluteExpiration(CacheConfig.AbsoluteExpiration)
            );
        }

        return found;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Pseudonym>> CreateIfNotExistBatchAsync(
        IReadOnlyList<Pseudonym> pseudonyms,
        CancellationToken cancellationToken
    )
    {
        // Not cached: this per-key MemoryCache is designed around the single-key lookup above,
        // and it costs the batch's whole point (one round trip) to split it back into a per-key
        // cache check.
        return await Repository.CreateIfNotExistBatchAsync(pseudonyms, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Pseudonym>> ListByNamespaceAsync(
        string namespaceName,
        PseudonymPageCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        // Not cached: this is a bulk browsing query, not the single-key lookup this cache
        // (keyed by original_value) is designed for.
        return await Repository.ListByNamespaceAsync(
            namespaceName,
            cursor,
            pageSize,
            cancellationToken
        );
    }

    /// <inheritdoc/>
    public async Task<long> CountByNamespaceAsync(
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        return await Repository.CountByNamespaceAsync(namespaceName, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, long>> CountAllGroupedByNamespaceAsync(
        CancellationToken cancellationToken
    )
    {
        // Not cached: called once every few minutes by a background service, not a
        // per-request/per-key lookup this cache is designed for.
        return await Repository.CountAllGroupedByNamespaceAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<(IReadOnlyList<Pseudonym> Items, long TotalCount)> SearchByNamespaceAsync(
        string namespaceName,
        string? searchText,
        bool includeOriginalValueInSearch,
        int skip,
        int take,
        CancellationToken cancellationToken
    )
    {
        // Not cached: same reasoning as ListByNamespaceAsync above - a bulk browsing/search
        // query, not the single-key lookup this cache (keyed by original_value) is designed for.
        return await Repository.SearchByNamespaceAsync(
            namespaceName,
            searchText,
            includeOriginalValueInSearch,
            skip,
            take,
            cancellationToken
        );
    }

    /// <inheritdoc/>
    public async Task<Pseudonym?> FindByPseudonymValueAsync(
        string namespaceName,
        string pseudonymValue,
        CancellationToken cancellationToken
    )
    {
        // Not cached: this cache is keyed by original_value (the create path), not by
        // pseudonym_value, and reverse lookup is meant to be an infrequent, audited action
        // rather than a hot path worth adding a second cache key scheme for.
        return await Repository.FindByPseudonymValueAsync(
            namespaceName,
            pseudonymValue,
            cancellationToken
        );
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Pseudonym>> FindAllByPseudonymValuesAsync(
        string namespaceName,
        IReadOnlyCollection<string> pseudonymValues,
        CancellationToken cancellationToken
    )
    {
        // Not cached - same reasoning as FindByPseudonymValueAsync above.
        return await Repository.FindAllByPseudonymValuesAsync(
            namespaceName,
            pseudonymValues,
            cancellationToken
        );
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Pseudonym>> FindAllByOriginalValueAsync(
        string namespaceName,
        string originalValue,
        CancellationToken cancellationToken
    )
    {
        // Not cached: unlike the sequence-0 row FindFirstByOriginalValueAsync returns, a
        // multi-psn namespace's set of pseudonyms for one original value grows, and a cached copy
        // would go on returning the smaller set.
        return await Repository.FindAllByOriginalValueAsync(
            namespaceName,
            originalValue,
            cancellationToken
        );
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Pseudonym>> FindAllByOriginalValuesAsync(
        string namespaceName,
        IReadOnlyCollection<string> originalValues,
        CancellationToken cancellationToken
    )
    {
        // Not cached - same reasoning as FindAllByOriginalValueAsync above, and the import path
        // this backs needs the authoritative current state of every key it is about to write
        // anyway, which a cache could only ever make staler.
        return await Repository.FindAllByOriginalValuesAsync(
            namespaceName,
            originalValues,
            cancellationToken
        );
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Pseudonym>> CreateSetIfNotExistAsync(
        IReadOnlyList<Pseudonym> newSequenceCandidates,
        CancellationToken cancellationToken
    )
    {
        // Not cached - same reasoning as FindAllByOriginalValueAsync above.
        return await Repository.CreateSetIfNotExistAsync(newSequenceCandidates, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<string>> FilterExistingPseudonymValuesAsync(
        string namespaceName,
        IReadOnlyCollection<string> pseudonymValues,
        CancellationToken cancellationToken
    )
    {
        // Not cached: this cache is keyed by original_value, not pseudonym_value. A positive-only
        // cache would be safe here (a pseudonym that exists can't stop existing - there's no
        // per-pseudonym delete, and a namespace with children can't be deleted), but a *miss*
        // must never be cached, since the parent value legitimately appears moments later when a
        // caller creates it in the parent and then immediately chains into the child. Left
        // uncached until measurements justify the second key scheme.
        return await Repository.FilterExistingPseudonymValuesAsync(
            namespaceName,
            pseudonymValues,
            cancellationToken
        );
    }

    private readonly record struct FirstPseudonymKey(
        string Namespace,
        DateTimeOffset NamespaceCreatedAt,
        string OriginalValue
    );
}
