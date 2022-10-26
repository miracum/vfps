using Microsoft.Extensions.Caching.Memory;
using Vfps.Config;
using Vfps.Data.Models;

namespace Vfps.Data;

public class CachingNamespaceRepository : INamespaceRepository
{
    public CachingNamespaceRepository(PseudonymContext context, IMemoryCache memoryCache, CacheConfig cacheConfig)
    {
        MemoryCache = memoryCache;
        CacheConfig = cacheConfig;
        NamespaceRepository = new NamespaceRepository(context);
    }

    private IMemoryCache MemoryCache { get; }
    private CacheConfig CacheConfig { get; }
    private NamespaceRepository NamespaceRepository { get; }

    /// <inheritdoc/>
    public async Task<Namespace?> FindAsync(string namespaceName)
    {
        var cacheKey = $"namespaces.{namespaceName}";

        return await MemoryCache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.SetSize(1)
                .SetAbsoluteExpiration(CacheConfig.AbsoluteExpiration);

            return await NamespaceRepository.FindAsync(namespaceName);
        });
    }
}
