using System.Diagnostics.Metrics;
using FakeItEasy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vfps.Data.Models;
using Vfps.Metrics;
using Vfps.Tests.ServiceTests;

namespace Vfps.Tests.PseudonymCountMetricsTests;

public class PseudonymCountMetricsTests : ServiceTestBase
{
    // Short enough that a test can wait for the background service's first tick without a slow test
    // run - same pattern (and rationale) as StalledPseudonymizationJobWatchdogServiceTests'.
    private static readonly TimeSpan TestPollInterval = TimeSpan.FromMilliseconds(20);

    private async Task<string> CreateTestNamespaceAsync()
    {
        // A unique namespace name per test run keeps this independent of the shared, static
        // instrument - other tests publishing the same gauge under a different namespace tag don't
        // affect this one's observed values. A real row is required too - pseudonyms.namespace_name
        // has a foreign key constraint to namespaces.name.
        var namespaceName = $"count-metrics-test-{Guid.NewGuid():N}";
        InMemoryPseudonymContext.Namespaces.Add(
            new Data.Models.Namespace
            {
                Name = namespaceName,
                Description = "count metrics test namespace",
                PseudonymLength = 16,
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
            }
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        return namespaceName;
    }

    private async Task AddPseudonymAsync(string namespaceName, string originalValue)
    {
        InMemoryPseudonymContext.Pseudonyms.Add(
            new Data.Models.Pseudonym
            {
                NamespaceName = namespaceName,
                OriginalValue = originalValue,
                PseudonymValue = $"psn-{originalValue}",
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
            }
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();
    }

    private PseudonymCountMetrics CreateSut(
        IPseudonymRepository? pseudonymRepository = null,
        TimeSpan? recomputeInterval = null
    ) =>
        new(
            pseudonymRepository ?? new PseudonymRepository(InMemoryPseudonymContext),
            new MetricSnapshotRepository(InMemoryPseudonymContext),
            NullLogger<PseudonymCountMetrics>.Instance,
            recomputeInterval
        );

    /// <summary>
    /// Collects one observation from the "vfps.pseudonyms" observable gauge, keyed by its namespace
    /// tag. The instrument is static, so this observes whatever the most recent refresh published -
    /// hence the unique namespace names above.
    /// </summary>
    private static Dictionary<string, long> ObserveGauge()
    {
        var observed = new Dictionary<string, long>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter == Program.Meter && instrument.Name == "vfps.pseudonyms")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, measurement, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "namespace" && tag.Value is string namespaceName)
                    {
                        observed[namespaceName] = measurement;
                    }
                }
            }
        );
        listener.Start();
        listener.RecordObservableInstruments();

        return observed;
    }

    [Fact]
    public async Task RefreshAsync_ShouldPublishCountsPerNamespace()
    {
        var namespaceA = await CreateTestNamespaceAsync();
        var namespaceB = await CreateTestNamespaceAsync();
        await AddPseudonymAsync(namespaceA, "a1");
        await AddPseudonymAsync(namespaceA, "a2");
        await AddPseudonymAsync(namespaceB, "b1");

        await CreateSut().RefreshAsync(TestContext.Current.CancellationToken);

        var observed = ObserveGauge();
        observed[namespaceA].Should().Be(2);
        observed[namespaceB].Should().Be(1);
    }

    [Fact]
    public async Task RefreshAsync_WhenAnotherReplicaHoldsTheClaim_ShouldPublishWithoutQuerying()
    {
        // The whole point of the shared snapshot: a replica that didn't win the claim still exports
        // the same values, without paying for the count query itself.
        var namespaceName = await CreateTestNamespaceAsync();
        await AddPseudonymAsync(namespaceName, "v1");

        // Stands in for the replica that won the claim and stored the result.
        await CreateSut(recomputeInterval: TimeSpan.FromMinutes(5))
            .RefreshAsync(TestContext.Current.CancellationToken);

        var otherReplicaRepository = A.Fake<IPseudonymRepository>();
        await CreateSut(otherReplicaRepository, TimeSpan.FromMinutes(5))
            .RefreshAsync(TestContext.Current.CancellationToken);

        A.CallTo(() =>
                otherReplicaRepository.CountAllGroupedByNamespaceAsync(A<CancellationToken>._)
            )
            .MustNotHaveHappened();
        ObserveGauge()[namespaceName].Should().Be(1);
    }

    [Fact]
    public async Task RefreshAsync_ForANamespaceWithNoPseudonymsLeft_ShouldStopReportingIt()
    {
        // The snapshot is replaced wholesale rather than merged, so a namespace that drops out of
        // the query result stops being reported instead of staying pinned at its last count - the
        // reason this uses an ObservableGauge rather than recording into a plain Gauge.
        var namespaceName = await CreateTestNamespaceAsync();
        await AddPseudonymAsync(namespaceName, "only-value");

        // TimeSpan.Zero so every call is due to recompute rather than being refused by its own
        // previous claim.
        var sut = CreateSut(recomputeInterval: TimeSpan.Zero);

        await sut.RefreshAsync(TestContext.Current.CancellationToken);
        ObserveGauge().Should().ContainKey(namespaceName);

        await InMemoryPseudonymContext
            .Pseudonyms.Where(pseudonym => pseudonym.NamespaceName == namespaceName)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await sut.RefreshAsync(TestContext.Current.CancellationToken);

        ObserveGauge().Should().NotContainKey(namespaceName);
    }

    [Fact]
    public async Task BackgroundService_ShouldRefreshOnItsOwnTimer()
    {
        var namespaceName = await CreateTestNamespaceAsync();
        await AddPseudonymAsync(namespaceName, "v1");
        await AddPseudonymAsync(namespaceName, "v2");

        var services = new ServiceCollection();
        services.AddSingleton<IPseudonymRepository>(
            new PseudonymRepository(InMemoryPseudonymContext)
        );
        services.AddSingleton<IMetricSnapshotRepository>(
            new MetricSnapshotRepository(InMemoryPseudonymContext)
        );
        services.AddLogging();
        services.AddScoped<PseudonymCountMetrics>();
        await using var serviceProvider = services.BuildServiceProvider();

        var sut = new PseudonymCountMetricsBackgroundService(serviceProvider, TestPollInterval);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        ObserveGauge()[namespaceName].Should().Be(2);
    }

    [Fact]
    public async Task RefreshAsync_WhenTheSnapshotIsUnreadable_ShouldNotThrow()
    {
        // Best-effort by design: a metrics refresh must never be the thing that takes a replica
        // down, so a failing repository is logged and swallowed.
        var snapshotRepository = A.Fake<IMetricSnapshotRepository>();
        A.CallTo(() =>
                snapshotRepository.TryClaimRefreshAsync(
                    A<string>._,
                    A<TimeSpan>._,
                    A<CancellationToken>._
                )
            )
            .Throws(new InvalidOperationException("database is down"));

        var sut = new PseudonymCountMetrics(
            new PseudonymRepository(InMemoryPseudonymContext),
            snapshotRepository,
            NullLogger<PseudonymCountMetrics>.Instance
        );

        var act = async () => await sut.RefreshAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }
}
