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

    private PseudonymCountMetrics CreateSut(IPseudonymRepository? pseudonymRepository = null) =>
        new(
            pseudonymRepository ?? new PseudonymRepository(InMemoryPseudonymContext),
            new PseudonymCountRepository(InMemoryPseudonymContext),
            NullLogger<PseudonymCountMetrics>.Instance
        );

    /// <summary>
    /// One full cycle: the recompute the Hangfire recurring job runs, then the snapshot read every
    /// replica does on its own timer. Separate calls in production, on different machines - so a
    /// test that wants "the gauge now reflects the database" has to do both.
    /// </summary>
    private async Task RecomputeAndPublishAsync(PseudonymCountMetrics? sut = null)
    {
        sut ??= CreateSut();
        await sut.RecomputeAsync(TestContext.Current.CancellationToken);
        await sut.PublishFromSnapshotAsync(TestContext.Current.CancellationToken);
    }

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
    public async Task RecomputeAsync_ShouldPublishCountsPerNamespace()
    {
        var namespaceA = await CreateTestNamespaceAsync();
        var namespaceB = await CreateTestNamespaceAsync();
        await AddPseudonymAsync(namespaceA, "a1");
        await AddPseudonymAsync(namespaceA, "a2");
        await AddPseudonymAsync(namespaceB, "b1");

        await RecomputeAndPublishAsync();

        var observed = ObserveGauge();
        observed[namespaceA].Should().Be(2);
        observed[namespaceB].Should().Be(1);
    }

    [Fact]
    public async Task PublishFromSnapshotAsync_OnAReplicaThatDidNotRecompute_ShouldStillExportTheCounts()
    {
        // The whole point of the shared snapshot: a replica that never ran the recurring job still
        // exports the same values, without paying for the count query itself. That's what makes the
        // series agree across pods instead of only existing on whichever one Hangfire picked.
        var namespaceName = await CreateTestNamespaceAsync();
        await AddPseudonymAsync(namespaceName, "v1");

        // Stands in for the replica Hangfire dispatched the recurring job to.
        await CreateSut().RecomputeAsync(TestContext.Current.CancellationToken);

        var otherReplicaRepository = A.Fake<IPseudonymRepository>();
        await CreateSut(otherReplicaRepository)
            .PublishFromSnapshotAsync(TestContext.Current.CancellationToken);

        A.CallTo(() =>
                otherReplicaRepository.CountAllGroupedByNamespaceAsync(A<CancellationToken>._)
            )
            .MustNotHaveHappened();
        ObserveGauge()[namespaceName].Should().Be(1);
    }

    [Fact]
    public async Task RecomputeAsync_ForANamespaceWithNoPseudonymsLeft_ShouldReportZero()
    {
        // An existing namespace is always reported, even at zero: "this namespace exists and is
        // empty" is a fact worth being able to see on a dashboard, and it distinguishes an empty
        // namespace from one that has been deleted (which does drop out - see below).
        var namespaceName = await CreateTestNamespaceAsync();
        await AddPseudonymAsync(namespaceName, "only-value");
        var sut = CreateSut();

        await RecomputeAndPublishAsync(sut);
        ObserveGauge()[namespaceName].Should().Be(1);

        await InMemoryPseudonymContext
            .Pseudonyms.Where(pseudonym => pseudonym.NamespaceName == namespaceName)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await RecomputeAndPublishAsync(sut);

        ObserveGauge()[namespaceName].Should().Be(0);
    }

    [Fact]
    public async Task RecomputeAsync_ForADeletedNamespace_ShouldStopReportingIt()
    {
        // The snapshot is replaced wholesale rather than merged, so a namespace that no longer
        // exists stops being reported instead of staying pinned at its last count - the reason this
        // uses an ObservableGauge rather than recording into a plain Gauge.
        var namespaceName = await CreateTestNamespaceAsync();
        await AddPseudonymAsync(namespaceName, "only-value");
        var sut = CreateSut();

        await RecomputeAndPublishAsync(sut);
        ObserveGauge().Should().ContainKey(namespaceName);

        await InMemoryPseudonymContext
            .Namespaces.Where(ns => ns.Name == namespaceName)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await RecomputeAndPublishAsync(sut);

        ObserveGauge().Should().NotContainKey(namespaceName);
    }

    [Fact]
    public async Task BackgroundService_ShouldPublishTheStoredSnapshotOnItsOwnTimer()
    {
        var namespaceName = await CreateTestNamespaceAsync();
        await AddPseudonymAsync(namespaceName, "v1");
        await AddPseudonymAsync(namespaceName, "v2");

        // Stands in for the Hangfire recurring job having run on some replica: the background
        // service only reads, so without stored counts there'd be nothing for it to publish.
        await CreateSut().RecomputeAsync(TestContext.Current.CancellationToken);

        var services = new ServiceCollection();
        services.AddSingleton<IPseudonymRepository>(
            new PseudonymRepository(InMemoryPseudonymContext)
        );
        services.AddSingleton<IPseudonymCountRepository>(
            new PseudonymCountRepository(InMemoryPseudonymContext)
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
    public async Task PublishFromSnapshotAsync_WhenTheCountsAreUnreadable_ShouldNotThrow()
    {
        // Best-effort by design: this runs on a timer on every replica, so a failing read must never
        // be the thing that takes one down. Logged and swallowed.
        var countRepository = A.Fake<IPseudonymCountRepository>();
        A.CallTo(() => countRepository.GetAllAsync(A<CancellationToken>._))
            .Throws(new InvalidOperationException("database is down"));

        var sut = new PseudonymCountMetrics(
            new PseudonymRepository(InMemoryPseudonymContext),
            countRepository,
            NullLogger<PseudonymCountMetrics>.Instance
        );

        var act = async () =>
            await sut.PublishFromSnapshotAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecomputeAsync_WhenTheCountsAreUnwritable_ShouldThrow()
    {
        // The deliberate asymmetry with PublishFromSnapshotAsync above: this one is a Hangfire job,
        // and throwing is how the failure reaches the dashboard instead of only the logs. Swallowing
        // here would leave a permanently stale gauge looking perfectly healthy.
        var countRepository = A.Fake<IPseudonymCountRepository>();
        A.CallTo(() =>
                countRepository.ReplaceAllAsync(
                    A<IReadOnlyDictionary<string, long>>._,
                    A<DateTimeOffset>._,
                    A<CancellationToken>._
                )
            )
            .Throws(new InvalidOperationException("database is down"));

        var sut = new PseudonymCountMetrics(
            new PseudonymRepository(InMemoryPseudonymContext),
            countRepository,
            NullLogger<PseudonymCountMetrics>.Instance
        );

        var act = async () => await sut.RecomputeAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
