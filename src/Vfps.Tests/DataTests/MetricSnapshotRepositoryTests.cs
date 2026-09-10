using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;

namespace Vfps.Tests.DataTests;

public class MetricSnapshotRepositoryTests : ServiceTests.ServiceTestBase
{
    private MetricSnapshotRepository CreateSut() => new(InMemoryPseudonymContext);

    [Fact]
    public async Task WriteAsync_ThenReadAsync_ShouldRoundTripTheValues()
    {
        var sut = CreateSut();
        var values = new Dictionary<string, long> { ["alpha"] = 3, ["beta"] = 7 };

        await sut.WriteAsync(
            MetricSnapshot.PseudonymCountsName,
            values,
            TestContext.Current.CancellationToken
        );
        var read = await sut.ReadAsync(
            MetricSnapshot.PseudonymCountsName,
            TestContext.Current.CancellationToken
        );

        read.Should().BeEquivalentTo(values);
    }

    [Fact]
    public async Task WriteAsync_ShouldReplaceTheStoredValuesWholesale()
    {
        // Not a merge: a series that's gone from the new set has to actually disappear, which is
        // what lets a deleted namespace stop being exported rather than staying pinned forever.
        var sut = CreateSut();
        await sut.WriteAsync(
            MetricSnapshot.PseudonymCountsName,
            new Dictionary<string, long> { ["gone"] = 1, ["kept"] = 2 },
            TestContext.Current.CancellationToken
        );

        await sut.WriteAsync(
            MetricSnapshot.PseudonymCountsName,
            new Dictionary<string, long> { ["kept"] = 5 },
            TestContext.Current.CancellationToken
        );

        var read = await sut.ReadAsync(
            MetricSnapshot.PseudonymCountsName,
            TestContext.Current.CancellationToken
        );
        read.Should().BeEquivalentTo(new Dictionary<string, long> { ["kept"] = 5 });
    }

    [Fact]
    public async Task WriteAsync_ShouldStampComputedAt()
    {
        // The seeded value is DateTimeOffset.MinValue ("never computed"). Nothing schedules off this
        // column any more - Hangfire owns the interval - but it's what an operator checks to tell a
        // genuinely low count from a recompute that's been failing, so it has to actually move.
        var before = DateTimeOffset.UtcNow;

        await CreateSut()
            .WriteAsync(
                MetricSnapshot.PseudonymCountsName,
                new Dictionary<string, long> { ["alpha"] = 1 },
                TestContext.Current.CancellationToken
            );

        var snapshot = await InMemoryPseudonymContext.MetricSnapshots.SingleAsync(
            candidate => candidate.Name == MetricSnapshot.PseudonymCountsName,
            TestContext.Current.CancellationToken
        );
        snapshot.ComputedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task WriteAsync_ForAnUnknownSnapshot_ShouldAffectNothing()
    {
        // No row is created for an unrecognised name - snapshot rows are seeded via HasData, so a
        // name with no row is a bug in the caller, not a row waiting to be inserted.
        await CreateSut()
            .WriteAsync(
                "no-such-snapshot",
                new Dictionary<string, long> { ["alpha"] = 1 },
                TestContext.Current.CancellationToken
            );

        var read = await CreateSut()
            .ReadAsync("no-such-snapshot", TestContext.Current.CancellationToken);
        read.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_ForAnUnknownSnapshot_ShouldReturnNoValues()
    {
        var read = await CreateSut()
            .ReadAsync("no-such-snapshot", TestContext.Current.CancellationToken);

        read.Should().BeEmpty();
    }
}
