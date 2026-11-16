using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Vfps.Metrics;

namespace Vfps.Tests.WebAppTests;

public class CachingEnabledTestFactory : IntegrationTestFactory<Program, PseudonymContext>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Pseudonymization:Caching:Namespaces:IsEnabled", "true");
        builder.UseSetting("Pseudonymization:Caching:Pseudonyms:IsEnabled", "true");
    }
}

/// <summary>
/// The cache's metrics come from MemoryCache itself, not from vfps code, so what's worth testing
/// is the wiring in Program.cs: that the cache it registers is built with an IMeterFactory and
/// TrackStatistics, and publishes under the meter name the OpenTelemetry pipeline subscribes to.
/// </summary>
public class MemoryCacheMetricsTests(CachingEnabledTestFactory factory)
    : IClassFixture<CachingEnabledTestFactory>
{
    [Fact]
    public void RegisteredCache_ShouldPublishRequestMetricsUnderTheSubscribedMeter()
    {
        var cache = factory.Services.GetRequiredService<IMemoryCache>();

        long hits = 0;
        long misses = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (
                instrument.Meter.Name == MetricsConfigurationExtensions.MemoryCacheMeterName
                && instrument.Name == "dotnet.cache.requests"
                && instrument.Meter.Tags?.Any(t =>
                    t.Key == "dotnet.cache.name" && Equals(t.Value, "vfps")
                ) == true
            )
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, measurement, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key != "dotnet.cache.request.result")
                    {
                        continue;
                    }

                    if (Equals(tag.Value, "hit"))
                    {
                        hits = measurement;
                    }
                    else if (Equals(tag.Value, "miss"))
                    {
                        misses = measurement;
                    }
                }
            }
        );
        listener.Start();

        var missesBefore = Observe(listener, () => misses);
        var hitsBefore = Observe(listener, () => hits);

        cache.Set(
            nameof(MemoryCacheMetricsTests),
            "value",
            new MemoryCacheEntryOptions().SetSize(1)
        );
        cache.TryGetValue(nameof(MemoryCacheMetricsTests), out _);
        cache.TryGetValue("missing-key", out _);

        Observe(listener, () => hits).Should().Be(hitsBefore + 1);
        Observe(listener, () => misses).Should().Be(missesBefore + 1);
    }

    private static long Observe(MeterListener listener, Func<long> read)
    {
        listener.RecordObservableInstruments();
        return read();
    }
}
