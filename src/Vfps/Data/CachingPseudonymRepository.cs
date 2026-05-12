using Microsoft.EntityFrameworkCore;
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

    public async Task<bool> DeleteAsync(
        string namespaceName,
        string pseudonymValue,
        CancellationToken cancellationToken
    )
    {
        // Resolve the original value before deletion so we can evict the cache entry, which is
        // keyed by original value (matching the key used in CreateIfNotExist).
        var originalValue = await Context
            .Pseudonyms.Where(p =>
                p.NamespaceName == namespaceName && p.PseudonymValue == pseudonymValue
            )
            .Select(p => p.OriginalValue)
            .FirstOrDefaultAsync(cancellationToken);

        var deleted = await Repository.DeleteAsync(namespaceName, pseudonymValue, cancellationToken);

        if (deleted && originalValue is not null)
        {
            var cacheKey = $"pseudonyms.{originalValue}@{namespaceName}";
            MemoryCache.Remove(cacheKey);
        }

        return deleted;
    }
}
