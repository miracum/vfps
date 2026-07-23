using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Prometheus;

namespace Vfps.Tests.MemoryCacheMetricsBackgroundServiceTests;

public class MemoryCacheMetricsBackgroundServiceTests
{
    [Fact]
    public async Task MemoryCacheMetricsBackgroundService_ShouldStartAndStopWithoutException()
    {
        var memoryCache = new MemoryCache(
            new MemoryCacheOptions { TrackStatistics = true, SizeLimit = 32 }
        );

        var sut = new MemoryCacheMetricsBackgroundService(memoryCache);
        var cancelToken = new CancellationToken();

        await sut.StartAsync(cancelToken);

        await sut.StopAsync(cancelToken);

        var memoryStream = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(
            memoryStream,
            TestContext.Current.CancellationToken
        );

        var text = Encoding.UTF8.GetString(memoryStream.ToArray());
        text.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPopulateGaugesFromMemoryCacheStatistics()
    {
        // The "starts/stops without exception" test above never actually lets a tick's worth of
        // ExecuteAsync's loop body run before cancelling - this uses a short injectable interval
        // (see the constructor's own comment) to actually observe one, and asserts the gauges it
        // sets rather than just that *something* got exported.
        using var memoryCache = new MemoryCache(
            new MemoryCacheOptions { TrackStatistics = true, SizeLimit = 32 }
        );
        memoryCache.Set("key", "value", new MemoryCacheEntryOptions().SetSize(1));
        memoryCache.TryGetValue("key", out _); // a hit
        memoryCache.TryGetValue("missing-key", out _); // a miss

        var sut = new MemoryCacheMetricsBackgroundService(
            memoryCache,
            TimeSpan.FromMilliseconds(20)
        );

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Metrics.CreateGauge returns the same already-registered collector for a name/help
        // combo it's seen before, rather than a new one - this is how the values the service's
        // own private static gauges were set to become observable here.
        var entriesGauge = Metrics.CreateGauge(
            "vfps_cache_entries",
            "Number of entries in the cache."
        );
        var missesGauge = Metrics.CreateGauge("vfps_cache_misses_total", "Number of cache misses.");
        var hitsGauge = Metrics.CreateGauge("vfps_cache_hits_total", "Number of cache hits.");

        entriesGauge.Value.Should().Be(1);
        hitsGauge.Value.Should().BeGreaterThanOrEqualTo(1);
        missesGauge.Value.Should().BeGreaterThanOrEqualTo(1);
    }
}
