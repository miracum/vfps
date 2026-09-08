using Microsoft.Extensions.Caching.Memory;
using Vfps.Config;
using Vfps.Data.Models;

namespace Vfps.Data;

public class CachingNamespaceRepository(
    PseudonymContext context,
    IMemoryCache memoryCache,
    CacheConfig cacheConfig
) : INamespaceRepository
{
    private CacheConfig CacheConfig { get; } = cacheConfig;
    private NamespaceRepository NamespaceRepository { get; } = new NamespaceRepository(context);

    /// <inheritdoc/>
    public async Task<Namespace> CreateAsync(
        Namespace @namespace,
        CancellationToken cancellationToken
    )
    {
        return await NamespaceRepository.CreateAsync(@namespace, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Namespace?> FindAsync(
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        var cacheKey = $"namespaces.{namespaceName}";

        if (memoryCache.TryGetValue(cacheKey, out Namespace? cached))
        {
            return cached;
        }

        var found = await NamespaceRepository.FindAsync(namespaceName, cancellationToken);

        // Only cache a hit, never a miss - a miss just before creation (e.g.
        // InitNamespacesBackgroundService's own existence check, immediately followed by
        // CreateAsync) would otherwise poison this key for the full cache lifetime, making a
        // namespace that visibly exists (GetAllAsync isn't cached) invisible to every
        // single-namespace lookup - which is exactly what Create/List/Browse use - until
        // expiry. A hit never goes stale from an edit (namespaces are immutable once created),
        // only from a deletion - which is why DeleteAsync below explicitly evicts this key
        // rather than relying on it to expire naturally.
        if (found is not null)
        {
            memoryCache.Set(
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
    public async Task<IReadOnlyList<Namespace>> GetAllAsync(CancellationToken cancellationToken)
    {
        // Not cached: namespace cardinality is low and this isn't on a hot path the way
        // single-namespace lookups (used on every pseudonym Create/List) are.
        return await NamespaceRepository.GetAllAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Namespace>> ListChildrenAsync(
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        // Not cached, for the same reason as GetAllAsync: low cardinality, and not on the
        // pseudonym create/list hot path that the single-namespace cache exists for.
        return await NamespaceRepository.ListChildrenAsync(namespaceName, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> HasChildrenAsync(
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        // Deliberately uncached: this gates namespace deletion, so a stale "no children" answer
        // would defeat the check it exists to enforce.
        return await NamespaceRepository.HasChildrenAsync(namespaceName, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string namespaceName, CancellationToken cancellationToken)
    {
        await NamespaceRepository.DeleteAsync(namespaceName, cancellationToken);

        // Evict rather than leave the deleted namespace's cached hit to expire naturally -
        // otherwise every FindAsync call (Create/List/Browse's write- and read-access checks
        // included) would keep seeing a namespace that no longer exists until the cache entry's
        // absolute expiration passes.
        memoryCache.Remove($"namespaces.{namespaceName}");
    }
}
