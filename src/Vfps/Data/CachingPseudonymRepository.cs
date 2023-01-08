using Microsoft.Extensions.Caching.Memory;
using Vfps.Config;
using Vfps.Data.Models;

namespace Vfps.Data;

public class CachingPseudonymRepository : IPseudonymRepository
{
    public CachingPseudonymRepository(
        PseudonymContext context,
        IMemoryCache memoryCache,
        CacheConfig cacheConfig
    )
    {
        MemoryCache = memoryCache;
        CacheConfig = cacheConfig;
        Context = context;

        Repository = new PseudonymRepository(context);
    }

    public IMemoryCache MemoryCache { get; }
    public CacheConfig CacheConfig { get; }
    public PseudonymContext Context { get; }
    public PseudonymRepository Repository { get; }

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
}
