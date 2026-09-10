using Microsoft.EntityFrameworkCore;

namespace Vfps.Tests.DataTests;

public class PseudonymCountRepositoryTests : ServiceTests.ServiceTestBase
{
    private PseudonymCountRepository CreateSut() => new(InMemoryPseudonymContext);

    /// <summary>
    /// Counts carry a foreign key to the namespace they belong to, so a row can only exist for a
    /// namespace that does.
    /// </summary>
    private async Task<string> CreateNamespaceAsync()
    {
        var name = $"count-repo-test-{Guid.NewGuid():N}";
        InMemoryPseudonymContext.Namespaces.Add(
            new Vfps.Data.Models.Namespace
            {
                Name = name,
                PseudonymLength = 16,
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
            }
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();
        return name;
    }

    [Fact]
    public async Task ReplaceAllAsync_ThenGetAllAsync_ShouldRoundTripTheCounts()
    {
        var alpha = await CreateNamespaceAsync();
        var beta = await CreateNamespaceAsync();

        await CreateSut()
            .ReplaceAllAsync(
                new Dictionary<string, long> { [alpha] = 3, [beta] = 7 },
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken
            );

        var read = await CreateSut().GetAllAsync(TestContext.Current.CancellationToken);
        read[alpha].Should().Be(3);
        read[beta].Should().Be(7);
    }

    [Fact]
    public async Task ReplaceAllAsync_ShouldUpdateACountThatChanged()
    {
        var alpha = await CreateNamespaceAsync();
        var sut = CreateSut();

        await sut.ReplaceAllAsync(
            new Dictionary<string, long> { [alpha] = 1 },
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken
        );
        await sut.ReplaceAllAsync(
            new Dictionary<string, long> { [alpha] = 42 },
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken
        );

        var read = await sut.GetAllAsync(TestContext.Current.CancellationToken);
        read[alpha].Should().Be(42);
    }

    [Fact]
    public async Task ReplaceAllAsync_ShouldRemoveANamespaceThatDroppedOutOfTheSet()
    {
        // Wholesale, not a merge: whatever is missing from the new set has to go rather than stay
        // pinned at its last count. In practice the caller passes every existing namespace (empty
        // ones included, at zero), so this is what removes a namespace that has been deleted.
        var kept = await CreateNamespaceAsync();
        var gone = await CreateNamespaceAsync();
        var sut = CreateSut();

        await sut.ReplaceAllAsync(
            new Dictionary<string, long> { [kept] = 2, [gone] = 1 },
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken
        );
        await sut.ReplaceAllAsync(
            new Dictionary<string, long> { [kept] = 5 },
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken
        );

        var read = await sut.GetAllAsync(TestContext.Current.CancellationToken);
        read.Should().ContainKey(kept).WhoseValue.Should().Be(5);
        read.Should().NotContainKey(gone);
    }

    [Fact]
    public async Task ReplaceAllAsync_ShouldStampComputedAt()
    {
        // Nothing schedules off this column - Hangfire owns the interval - but it's what tells an
        // operator whether a suspicious count is current or the recompute has been failing.
        var alpha = await CreateNamespaceAsync();
        var computedAt = DateTimeOffset.UtcNow;

        await CreateSut()
            .ReplaceAllAsync(
                new Dictionary<string, long> { [alpha] = 1 },
                computedAt,
                TestContext.Current.CancellationToken
            );

        var row = await InMemoryPseudonymContext
            .PseudonymCounts.AsNoTracking()
            .SingleAsync(c => c.NamespaceName == alpha, TestContext.Current.CancellationToken);
        row.ComputedAt.Should().BeCloseTo(computedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DeletingANamespace_ShouldTakeItsCountWithIt()
    {
        // The cascade is what makes a deleted namespace stop being exported immediately, rather
        // than lingering as a series until the next recompute sweeps it.
        var alpha = await CreateNamespaceAsync();
        await CreateSut()
            .ReplaceAllAsync(
                new Dictionary<string, long> { [alpha] = 4 },
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken
            );

        await InMemoryPseudonymContext
            .Namespaces.Where(n => n.Name == alpha)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        var read = await CreateSut().GetAllAsync(TestContext.Current.CancellationToken);
        read.Should().NotContainKey(alpha);
    }

    [Fact]
    public async Task GetAllAsync_WithNothingStored_ShouldReturnNoCounts()
    {
        var read = await CreateSut().GetAllAsync(TestContext.Current.CancellationToken);

        read.Should().BeEmpty();
    }
}
