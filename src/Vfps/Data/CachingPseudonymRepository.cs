using Microsoft.Extensions.Caching.Memory;
using Vfps.Config;
using Vfps.Data.Models;

namespace Vfps.Data;

public class CachingPseudonymRepository(
    PseudonymContext context,
    IMemoryCache memoryCache,
    CacheConfig cacheConfig
) : IPseudonymRepository
{
    public IMemoryCache MemoryCache { get; } = memoryCache;
    public CacheConfig CacheConfig { get; } = cacheConfig;
    public PseudonymContext Context { get; } = context;
    public PseudonymRepository Repository { get; } = new PseudonymRepository(context);

    public async Task<Pseudonym?> CreateIfNotExist(Pseudonym pseudonym)
    {
        var cacheKey = $"pseudonyms.{pseudonym.OriginalValue}@{pseudonym.NamespaceName}";

        return await MemoryCache.GetOrCreateAsync(
            cacheKey,
            async entry =>
            {
                entry.SetSize(1).SetAbsoluteExpiration(CacheConfig.AbsoluteExpiration);

                return await Repository.CreateIfNotExist(pseudonym);
            }
        );
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
    public async Task<Pseudonym?> FindByPseudonymValueAsync(
        string namespaceName,
        string pseudonymValue,
        CancellationToken cancellationToken
    )
    {
        // Not cached: this cache is keyed by original_value (the Create path), not by
        // pseudonym_value, and reverse lookup is meant to be an infrequent, audited action
        // rather than a hot path worth adding a second cache key scheme for.
        return await Repository.FindByPseudonymValueAsync(
            namespaceName,
            pseudonymValue,
            cancellationToken
        );
    }
}
