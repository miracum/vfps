using Microsoft.EntityFrameworkCore;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.Tests.DataTests;

public class PseudonymizationJobRepositoryTests : ServiceTests.ServiceTestBase
{
    private static PseudonymizationJob CreateJob(
        PseudonymizationJobStatus status,
        DateTimeOffset lastUpdatedAt,
        string createdBy = "test-user"
    )
    {
        var now = DateTimeOffset.UtcNow;
        return new PseudonymizationJob
        {
            Id = Guid.NewGuid(),
            Status = status,
            CreatedBy = createdBy,
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

    [Fact]
    public async Task DeleteFinishedAsync_WithoutCreatedByFilter_ShouldOnlyDeleteTerminalJobs()
    {
        var now = DateTimeOffset.UtcNow;
        var completed = CreateJob(PseudonymizationJobStatus.Completed, now);
        var failed = CreateJob(PseudonymizationJobStatus.Failed, now);
        var cancelled = CreateJob(PseudonymizationJobStatus.Cancelled, now);
        var stalled = CreateJob(PseudonymizationJobStatus.Stalled, now);
        var running = CreateJob(PseudonymizationJobStatus.Running, now);
        var queued = CreateJob(PseudonymizationJobStatus.Queued, now);
        var awaitingUpload = CreateJob(PseudonymizationJobStatus.AwaitingUpload, now);

        InMemoryPseudonymContext.PseudonymizationJobs.AddRange(
            completed,
            failed,
            cancelled,
            stalled,
            running,
            queued,
            awaitingUpload
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var sut = new PseudonymizationJobRepository(InMemoryPseudonymContext);

        var deletedCount = await sut.DeleteFinishedAsync(
            null,
            TestContext.Current.CancellationToken
        );

        deletedCount.Should().Be(4);
        var remainingIds = await InMemoryPseudonymContext
            .PseudonymizationJobs.Select(j => j.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        remainingIds.Should().BeEquivalentTo([running.Id, queued.Id, awaitingUpload.Id]);
    }

    [Fact]
    public async Task DeleteFinishedAsync_WithCreatedByFilter_ShouldOnlyDeleteThatUsersJobs()
    {
        var now = DateTimeOffset.UtcNow;
        var aliceCompleted = CreateJob(PseudonymizationJobStatus.Completed, now, "alice");
        var bobCompleted = CreateJob(PseudonymizationJobStatus.Completed, now, "bob");

        InMemoryPseudonymContext.PseudonymizationJobs.AddRange(aliceCompleted, bobCompleted);
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var sut = new PseudonymizationJobRepository(InMemoryPseudonymContext);

        var deletedCount = await sut.DeleteFinishedAsync(
            "alice",
            TestContext.Current.CancellationToken
        );

        deletedCount.Should().Be(1);
        var remaining = await InMemoryPseudonymContext.PseudonymizationJobs.ToListAsync(
            TestContext.Current.CancellationToken
        );
        remaining.Should().ContainSingle().Which.CreatedBy.Should().Be("bob");
    }
}
