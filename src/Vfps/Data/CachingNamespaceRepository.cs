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
        Context = context;
    }

    private IMemoryCache MemoryCache { get; }
    private CacheConfig CacheConfig { get; }
    private PseudonymContext Context { get; }

    /// <inheritdoc/>
    public async Task<Namespace?> FindAsync(string namespaceName)
    {
        return await MemoryCache.GetOrCreateAsync(namespaceName, async entry =>
        {
            entry.SetSize(1)
                .SetAbsoluteExpiration(CacheConfig.AbsoluteExpiration);

            return await Context.Namespaces.FindAsync(namespaceName);
        });
    }
}
