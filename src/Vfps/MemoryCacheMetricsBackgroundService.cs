using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace Vfps;

public class MemoryCacheMetricsBackgroundService(
    IMemoryCache memoryCache,
    TimeSpan? interval = null
) : BackgroundService
{
    // Only ever overridden by tests, which need a far shorter interval than the real 60s to
    // observe a tick without a slow test run - same pattern (and rationale) as
    // StalledPseudonymizationJobWatchdogService's own checkInterval constructor parameter.
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromSeconds(60);

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var stats = memoryCache.GetCurrentStatistics();
            if (stats is not null)
            {
                EntriesInCache.Set(stats.CurrentEntryCount);
                CacheMisses.Set(stats.TotalMisses);
                CacheHits.Set(stats.TotalHits);
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
