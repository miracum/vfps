using Vfps.Data.Models;

namespace Vfps.Tests.DataTests;

public class MetricSnapshotRepositoryTests : ServiceTests.ServiceTestBase
{
    private MetricSnapshotRepository CreateSut() => new(InMemoryPseudonymContext);

    [Fact]
    public async Task TryClaimRefreshAsync_OnTheSeededRow_ShouldSucceedOnceAndThenRefuse()
    {
        // The seeded ComputedAt is DateTimeOffset.MinValue ("never computed"), so the first caller
        // always wins - and having won, bumps the timestamp out of range for everyone else until
        // the interval has passed. This is the entire mechanism that stops every replica from
        // running the count query at once, so it's worth asserting directly.
        var sut = CreateSut();

        var first = await sut.TryClaimRefreshAsync(
            MetricSnapshot.PseudonymCountsName,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken
        );
        var second = await sut.TryClaimRefreshAsync(
            MetricSnapshot.PseudonymCountsName,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken
        );

        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    [Fact]
    public async Task TryClaimRefreshAsync_OnceTheIntervalHasPassed_ShouldSucceedAgain()
    {
        var sut = CreateSut();
        await sut.TryClaimRefreshAsync(
            MetricSnapshot.PseudonymCountsName,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken
        );

        // TimeSpan.Zero means "anything older than right now is due", which is how a caller a full
        // interval later sees the row.
        var claimedAgain = await sut.TryClaimRefreshAsync(
            MetricSnapshot.PseudonymCountsName,
            TimeSpan.Zero,
            TestContext.Current.CancellationToken
        );

        claimedAgain.Should().BeTrue();
    }

    [Fact]
    public async Task TryClaimRefreshAsync_ForAnUnknownSnapshot_ShouldRefuse()
    {
        var claimed = await CreateSut()
            .TryClaimRefreshAsync(
                "no-such-snapshot",
                TimeSpan.Zero,
                TestContext.Current.CancellationToken
            );

        claimed.Should().BeFalse();
    }

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
    public async Task ReadAsync_ForAnUnknownSnapshot_ShouldReturnNoValues()
    {
        var read = await CreateSut()
            .ReadAsync("no-such-snapshot", TestContext.Current.CancellationToken);

        read.Should().BeEmpty();
    }
}
