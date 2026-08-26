using System.Diagnostics.Metrics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

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

    // Dotted names are the OpenTelemetry convention; the Prometheus exporter renders them with
    // underscores on export (e.g. "vfps_cache_entries"), matching the metric names this service
    // exposed under prometheus-net.
    private static readonly Gauge<long> EntriesInCache = Program.Meter.CreateGauge<long>(
        "vfps.cache.entries",
        description: "Number of entries in the cache."
    );
    private static readonly Gauge<long> CacheMisses = Program.Meter.CreateGauge<long>(
        "vfps.cache.misses_total",
        description: "Number of cache misses."
    );
    private static readonly Gauge<long> CacheHits = Program.Meter.CreateGauge<long>(
        "vfps.cache.hits_total",
        description: "Number of cache hits."
    );

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var stats = memoryCache.GetCurrentStatistics();
            if (stats is not null)
            {
                EntriesInCache.Record(stats.CurrentEntryCount);
                CacheMisses.Record(stats.TotalMisses);
                CacheHits.Record(stats.TotalHits);
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
