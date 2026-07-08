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

        return await memoryCache.GetOrCreateAsync(
            cacheKey,
            async entry =>
            {
                entry.SetSize(1).SetAbsoluteExpiration(CacheConfig.AbsoluteExpiration);

                return await NamespaceRepository.FindAsync(namespaceName, cancellationToken);
            }
        );
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Namespace>> GetAllAsync(CancellationToken cancellationToken)
    {
        // Not cached: namespace cardinality is low and this isn't on a hot path the way
        // single-namespace lookups (used on every pseudonym Create/List) are.
        return await NamespaceRepository.GetAllAsync(cancellationToken);
    }
}
