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
}
