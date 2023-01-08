using Microsoft.Extensions.Caching.Memory;
using Prometheus;
using System.Text;

namespace Vfps.Tests.MemoryCacheMetricsBackgroundServiceTests;

public class MemoryCacheMetricsBackgroundServiceTests
{
    [Fact]
    public async Task MemoryCacheMetricsBackgroundService_ShouldStartAndStopWithoutException()
    {
        var memoryCache = new MemoryCache(
            new MemoryCacheOptions { TrackStatistics = true, SizeLimit = 32, }
        );

        var sut = new MemoryCacheMetricsBackgroundService(memoryCache);
        var cancelToken = new CancellationToken();

        await sut.StartAsync(cancelToken);

        await sut.StopAsync(cancelToken);

        var memoryStream = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(memoryStream);

        var text = Encoding.UTF8.GetString(memoryStream.ToArray());
        text.Should().NotBeEmpty();
    }
}
