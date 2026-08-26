using System.Diagnostics.Metrics;
using Microsoft.Extensions.Caching.Memory;

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

        var act = async () =>
        {
            await sut.StartAsync(cancelToken);
            await sut.StopAsync(cancelToken);
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPopulateGaugesFromMemoryCacheStatistics()
    {
        // The "starts/stops without exception" test above never actually lets a tick's worth of
        // ExecuteAsync's loop body run before cancelling - this uses a short injectable interval
        // (see the constructor's own comment) to actually observe one, and asserts the gauge
        // values it records via a MeterListener rather than just that *something* got exported.
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

        long? entries = null;
        long? hits = null;
        long? misses = null;

        // MemoryCacheMetricsBackgroundService.EntriesInCache/CacheHits/CacheMisses are static, so
        // this listener observes recordings from any instance of the service - matching the
        // existing "same already-registered collector for a name it's seen before" behavior this
        // codebase already relied on under prometheus-net.
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == Program.Meter)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, _, _) =>
            {
                switch (instrument.Name)
                {
                    case "vfps.cache.entries":
                        entries = measurement;
                        break;
                    case "vfps.cache.hits_total":
                        hits = measurement;
                        break;
                    case "vfps.cache.misses_total":
                        misses = measurement;
                        break;
                }
            }
        );
        listener.Start();

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        entries.Should().Be(1);
        hits.Should().BeGreaterThanOrEqualTo(1);
        misses.Should().BeGreaterThanOrEqualTo(1);
    }
}
