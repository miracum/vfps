using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vfps.Data;

namespace Vfps.Tests.DataTests;

public class PseudonymRepositoryMetricsTests : ServiceTests.ServiceTestBase
{
    private async Task<string> CreateTestNamespaceAsync()
    {
        // A unique namespace name per test run keeps this independent of the shared, static
        // Meter (Program.Meter) - other tests recording the same gauge under a different
        // namespace tag don't affect this one's observed values. A real row is required too -
        // pseudonyms.namespace_name has a foreign key constraint to namespaces.name.
        var namespaceName = $"metrics-test-{Guid.NewGuid():N}";
        InMemoryPseudonymContext.Namespaces.Add(
            new Data.Models.Namespace
            {
                Name = namespaceName,
                Description = "metrics test namespace",
                PseudonymLength = 16,
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
            }
        );
        await InMemoryPseudonymContext.SaveChangesAsync();
        InMemoryPseudonymContext.ChangeTracker.Clear();

        return namespaceName;
    }

    [Fact]
    public async Task CountAllGroupedByNamespaceAsync_ShouldReturnCountsPerNamespace()
    {
        var namespaceA = await CreateTestNamespaceAsync();
        var namespaceB = await CreateTestNamespaceAsync();
        var sut = new PseudonymRepository(InMemoryPseudonymContext);

        await sut.CreateIfNotExist(
            new Data.Models.Pseudonym
            {
                NamespaceName = namespaceA,
                OriginalValue = "a1",
                PseudonymValue = "pa1",
            }
        );
        await sut.CreateIfNotExist(
            new Data.Models.Pseudonym
            {
                NamespaceName = namespaceA,
                OriginalValue = "a2",
                PseudonymValue = "pa2",
            }
        );
        await sut.CreateIfNotExist(
            new Data.Models.Pseudonym
            {
                NamespaceName = namespaceB,
                OriginalValue = "b1",
                PseudonymValue = "pb1",
            }
        );

        var counts = await sut.CountAllGroupedByNamespaceAsync(CancellationToken.None);

        counts[namespaceA].Should().Be(2);
        counts[namespaceB].Should().Be(1);
    }

    [Fact]
    public async Task CountAllGroupedByNamespaceAsync_CalledTwiceForTheSameValue_ShouldNotDoubleCount()
    {
        // CreateIfNotExist upserts - a repeat call for the same (namespace, original_value)
        // returns the already-existing row rather than inserting a new one, so the true row
        // count (and thus this query's result) must not increase on the second call.
        var namespaceName = await CreateTestNamespaceAsync();
        var sut = new PseudonymRepository(InMemoryPseudonymContext);
        var pseudonym = new Data.Models.Pseudonym
        {
            NamespaceName = namespaceName,
            OriginalValue = "some value",
            PseudonymValue = "some-pseudonym",
        };

        await sut.CreateIfNotExist(pseudonym);
        await sut.CreateIfNotExist(pseudonym);

        var counts = await sut.CountAllGroupedByNamespaceAsync(CancellationToken.None);

        counts[namespaceName].Should().Be(1);
    }

    [Fact]
    public async Task PseudonymCountMetricsBackgroundService_ShouldSetGaugeFromDatabaseOnStartup()
    {
        var namespaceName = await CreateTestNamespaceAsync();
        var repository = new PseudonymRepository(InMemoryPseudonymContext);
        await repository.CreateIfNotExist(
            new Data.Models.Pseudonym
            {
                NamespaceName = namespaceName,
                OriginalValue = "v1",
                PseudonymValue = "p1",
            }
        );
        await repository.CreateIfNotExist(
            new Data.Models.Pseudonym
            {
                NamespaceName = namespaceName,
                OriginalValue = "v2",
                PseudonymValue = "p2",
            }
        );

        var services = new ServiceCollection();
        services.AddSingleton<IPseudonymRepository>(repository);
        await using var serviceProvider = services.BuildServiceProvider();

        var sut = new PseudonymCountMetricsBackgroundService(
            serviceProvider,
            NullLogger<PseudonymCountMetricsBackgroundService>.Instance
        );

        long? observedCount = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == Program.Meter && instrument.Name == "vfps.pseudonyms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, measurement, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "namespace" && (string?)tag.Value == namespaceName)
                    {
                        observedCount = measurement;
                    }
                }
            }
        );
        listener.Start();

        await sut.StartAsync(CancellationToken.None);
        // The background service refreshes once immediately on startup, before the first
        // PeriodicTimer tick - poll briefly rather than assuming a fixed delay is enough.
        for (var i = 0; i < 50 && observedCount is null; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        await sut.StopAsync(CancellationToken.None);

        observedCount.Should().Be(2);
    }
}
