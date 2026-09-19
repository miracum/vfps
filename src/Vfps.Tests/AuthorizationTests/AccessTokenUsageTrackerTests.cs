namespace Vfps.Tests.AuthorizationTests;

public class AccessTokenUsageTrackerTests
{
    [Fact]
    public void DrainPending_WithNothingTracked_ShouldBeEmpty()
    {
        var sut = new AccessTokenUsageTracker();

        sut.DrainPending().Should().BeEmpty();
    }

    [Fact]
    public void DrainPending_ShouldCollapseRepeatedUseIntoOneEntry()
    {
        var sut = new AccessTokenUsageTracker();
        var tokenId = Guid.NewGuid();
        var first = DateTimeOffset.UtcNow;

        sut.Track(tokenId, first);
        sut.Track(tokenId, first.AddSeconds(1));
        sut.Track(tokenId, first.AddSeconds(2));

        var pending = sut.DrainPending();

        pending.Should().HaveCount(1);
        pending[tokenId].Should().Be(first.AddSeconds(2));
    }

    [Fact]
    public void DrainPending_ShouldEmptyTheTracker()
    {
        var sut = new AccessTokenUsageTracker();
        sut.Track(Guid.NewGuid(), DateTimeOffset.UtcNow);

        sut.DrainPending().Should().HaveCount(1);
        sut.DrainPending().Should().BeEmpty();
    }

    [Fact]
    public void Track_DuringADrain_ShouldNotBeLost()
    {
        // The dictionary is swapped wholesale rather than drained key by key, so a request
        // authenticating mid-flush lands in the next batch instead of racing with the removal
        // of its own entry.
        var sut = new AccessTokenUsageTracker();
        var duringDrain = Guid.NewGuid();

        sut.Track(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var first = sut.DrainPending();
        sut.Track(duringDrain, DateTimeOffset.UtcNow);

        first.Should().HaveCount(1);
        sut.DrainPending().Should().ContainKey(duringDrain);
    }
}
