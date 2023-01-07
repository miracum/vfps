using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace Vfps;

public class MemoryCacheMetricsBackgroundService : BackgroundService
{
    private static readonly Gauge EntriesInCache = Metrics.CreateGauge(
        "vfps_cache_entries",
        "Number of entries in the cache."
    );
    private static readonly Gauge CacheMisses = Metrics.CreateGauge(
        "vfps_cache_misses_total",
        "Number of cache misses."
    );
    private static readonly Gauge CacheHits = Metrics.CreateGauge(
        "vfps_cache_hits_total",
        "Number of cache hits."
    );

    public MemoryCacheMetricsBackgroundService(IMemoryCache memoryCache)
    {
        MemoryCache = memoryCache;
    }

    private IMemoryCache MemoryCache { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var stats = MemoryCache.GetCurrentStatistics();
            if (stats is not null)
            {
                EntriesInCache.Set(stats.CurrentEntryCount);
                CacheMisses.Set(stats.TotalMisses);
                CacheHits.Set(stats.TotalHits);
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }
}
