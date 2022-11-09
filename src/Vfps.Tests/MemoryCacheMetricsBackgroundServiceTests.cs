using Microsoft.Extensions.Caching.Memory;

namespace Vfps.Tests.MemoryCacheMetricsBackgroundServiceTests;

public class MemoryCacheMetricsBackgroundServiceTests
{
    [Fact]
    public async Task MemoryCacheMetricsBackgroundService_ShouldStartAndStopWithoutException()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions
        {
            TrackStatistics = true,
            SizeLimit = 32,
        });
        var sut = new MemoryCacheMetricsBackgroundService(memoryCache);
        var cancelToken = new CancellationToken();

        await sut.StartAsync(cancelToken);

        await sut.StopAsync(cancelToken);
    }
}
