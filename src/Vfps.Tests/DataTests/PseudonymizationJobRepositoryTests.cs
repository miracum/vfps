using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.Tests.DataTests;

public class PseudonymizationJobRepositoryTests : ServiceTests.ServiceTestBase
{
    private static PseudonymizationJob CreateJob(
        PseudonymizationJobStatus status,
        DateTimeOffset lastUpdatedAt
    )
    {
        var now = DateTimeOffset.UtcNow;
        return new PseudonymizationJob
        {
            Id = Guid.NewGuid(),
            Status = status,
            CreatedBy = "test-user",
            InputObjectKey = "csv-jobs/input.csv",
            CreatedAt = now,
            LastUpdatedAt = lastUpdatedAt,
        };
    }

    [Fact]
    public async Task FindStalledRunningJobIdsAsync_ShouldOnlyReturnRunningJobsPastTheThreshold()
    {
        var now = DateTimeOffset.UtcNow;
        var staleRunning = CreateJob(PseudonymizationJobStatus.Running, now.AddMinutes(-20));
        var freshRunning = CreateJob(PseudonymizationJobStatus.Running, now.AddMinutes(-1));
        // Same age as staleRunning, but not Running - a stale-but-terminal (or Queued) job isn't
        // "stuck" in the sense this query cares about, so it must never be returned.
        var staleCompleted = CreateJob(PseudonymizationJobStatus.Completed, now.AddMinutes(-20));
        var staleQueued = CreateJob(PseudonymizationJobStatus.Queued, now.AddMinutes(-20));

        InMemoryPseudonymContext.PseudonymizationJobs.AddRange(
            staleRunning,
            freshRunning,
            staleCompleted,
            staleQueued
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var sut = new PseudonymizationJobRepository(InMemoryPseudonymContext);

        var stalledIds = await sut.FindStalledRunningJobIdsAsync(
            TimeSpan.FromMinutes(10),
            TestContext.Current.CancellationToken
        );

        stalledIds.Should().ContainSingle().Which.Should().Be(staleRunning.Id);
    }

    [Fact]
    public async Task FindStalledRunningJobIdsAsync_WithNoStalledJobs_ShouldReturnEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        var freshRunning = CreateJob(PseudonymizationJobStatus.Running, now.AddSeconds(-5));

        InMemoryPseudonymContext.PseudonymizationJobs.Add(freshRunning);
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var sut = new PseudonymizationJobRepository(InMemoryPseudonymContext);

        var stalledIds = await sut.FindStalledRunningJobIdsAsync(
            TimeSpan.FromMinutes(10),
            TestContext.Current.CancellationToken
        );

        stalledIds.Should().BeEmpty();
    }
}
