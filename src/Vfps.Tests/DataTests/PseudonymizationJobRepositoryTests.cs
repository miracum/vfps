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
    public async Task UpdateProgressUnlessCancelledAsync_WithRunningJob_ShouldPersistProgressAndReportActive()
    {
        var job = CreateJob(
            PseudonymizationJobStatus.Running,
            DateTimeOffset.UtcNow.AddMinutes(-1)
        );
        InMemoryPseudonymContext.PseudonymizationJobs.Add(job);
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var sut = new PseudonymizationJobRepository(ContextFactory);

        var stillActive = await sut.UpdateProgressUnlessCancelledAsync(
            job.Id,
            bytesProcessed: 4096,
            rowsProcessed: 200,
            badDataRowCount: 3,
            missingValueCount: 7,
            unresolvedValueCount: 0,
            TestContext.Current.CancellationToken
        );

        stillActive.Should().BeTrue();

        var stored = await InMemoryPseudonymContext
            .PseudonymizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        stored.BytesProcessed.Should().Be(4096);
        stored.RowsProcessed.Should().Be(200);
        stored.BadDataRowCount.Should().Be(3);
        stored.MissingValueCount.Should().Be(7);
        stored.LastUpdatedAt.Should().BeAfter(job.LastUpdatedAt);
    }

    [Fact]
    public async Task UpdateProgressUnlessCancelledAsync_WithCancelledJob_ShouldReportInactiveAndLeaveProgressAlone()
    {
        var job = CreateJob(
            PseudonymizationJobStatus.Cancelled,
            DateTimeOffset.UtcNow.AddMinutes(-1)
        );
        job.RowsProcessed = 17;
        InMemoryPseudonymContext.PseudonymizationJobs.Add(job);
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var sut = new PseudonymizationJobRepository(ContextFactory);

        var stillActive = await sut.UpdateProgressUnlessCancelledAsync(
            job.Id,
            bytesProcessed: 4096,
            rowsProcessed: 200,
            badDataRowCount: 0,
            missingValueCount: 0,
            unresolvedValueCount: 0,
            TestContext.Current.CancellationToken
        );

        // The "no rows matched" that signals the cancellation is the same thing that leaves the
        // row untouched - a cancelled job's final progress isn't overwritten by a runner that
        // hadn't noticed yet.
        stillActive.Should().BeFalse();

        var stored = await InMemoryPseudonymContext
            .PseudonymizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        stored.RowsProcessed.Should().Be(17);
    }

    [Theory]
    [InlineData(PseudonymizationJobStatus.Stalled)]
    [InlineData(PseudonymizationJobStatus.Running)]
    public async Task UpdateProgressUnlessCancelledAsync_WithNonCancelledStatus_ShouldReportActive(
        PseudonymizationJobStatus status
    )
    {
        // Stalled in particular: the watchdog's verdict is a heuristic a still-healthy job is
        // expected to overtake, so it must not stop a live runner the way Cancelled does.
        var job = CreateJob(status, DateTimeOffset.UtcNow.AddMinutes(-1));
        InMemoryPseudonymContext.PseudonymizationJobs.Add(job);
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var sut = new PseudonymizationJobRepository(ContextFactory);

        var stillActive = await sut.UpdateProgressUnlessCancelledAsync(
            job.Id,
            bytesProcessed: 1,
            rowsProcessed: 1,
            badDataRowCount: 0,
            missingValueCount: 0,
            unresolvedValueCount: 0,
            TestContext.Current.CancellationToken
        );

        stillActive.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateProgressUnlessCancelledAsync_WithUnknownJob_ShouldReportInactive()
    {
        var sut = new PseudonymizationJobRepository(ContextFactory);

        var stillActive = await sut.UpdateProgressUnlessCancelledAsync(
            Guid.NewGuid(),
            bytesProcessed: 1,
            rowsProcessed: 1,
            badDataRowCount: 0,
            missingValueCount: 0,
            unresolvedValueCount: 0,
            TestContext.Current.CancellationToken
        );

        stillActive.Should().BeFalse();
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

        var sut = new PseudonymizationJobRepository(ContextFactory);

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

        var sut = new PseudonymizationJobRepository(ContextFactory);

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

        var sut = new PseudonymizationJobRepository(ContextFactory);

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

        var sut = new PseudonymizationJobRepository(ContextFactory);

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

    // The conditional transitions below are what keeps two actors - possibly on different
    // replicas - from overwriting each other's status change: each one must apply from exactly
    // the statuses it is documented for, and leave the job untouched from every other.

    private async Task<PseudonymizationJob> SeedAsync(
        PseudonymizationJobStatus status,
        DateTimeOffset? lastUpdatedAt = null
    )
    {
        var job = CreateJob(status, lastUpdatedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1));
        InMemoryPseudonymContext.PseudonymizationJobs.Add(job);
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();
        return job;
    }

    private async Task<PseudonymizationJobStatus> StatusOfAsync(Guid id) =>
        (
            await InMemoryPseudonymContext
                .PseudonymizationJobs.AsNoTracking()
                .SingleAsync(j => j.Id == id, TestContext.Current.CancellationToken)
        ).Status;

    [Theory]
    [InlineData(PseudonymizationJobStatus.AwaitingUpload, true)]
    [InlineData(PseudonymizationJobStatus.Queued, false)]
    [InlineData(PseudonymizationJobStatus.Running, false)]
    [InlineData(PseudonymizationJobStatus.Completed, false)]
    [InlineData(PseudonymizationJobStatus.Failed, false)]
    [InlineData(PseudonymizationJobStatus.Cancelled, false)]
    [InlineData(PseudonymizationJobStatus.Stalled, false)]
    public async Task MarkQueuedAsync_ShouldOnlyApplyToAJobAwaitingItsUpload(
        PseudonymizationJobStatus status,
        bool shouldApply
    )
    {
        var job = await SeedAsync(status);
        var sut = new PseudonymizationJobRepository(ContextFactory);

        var applied = await sut.MarkQueuedAsync(
            job.Id,
            1024,
            TestContext.Current.CancellationToken
        );

        applied.Should().Be(shouldApply);
        (await StatusOfAsync(job.Id))
            .Should()
            .Be(shouldApply ? PseudonymizationJobStatus.Queued : status);
    }

    [Fact]
    public async Task MarkQueuedAsync_CalledTwice_ShouldOnlyApplyOnce()
    {
        // Two upload-complete requests for the same job: only the first may go on to enqueue it.
        var job = await SeedAsync(PseudonymizationJobStatus.AwaitingUpload);
        var sut = new PseudonymizationJobRepository(ContextFactory);

        var first = await sut.MarkQueuedAsync(job.Id, 1024, TestContext.Current.CancellationToken);
        var second = await sut.MarkQueuedAsync(job.Id, 1024, TestContext.Current.CancellationToken);

        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    [Theory]
    [InlineData(PseudonymizationJobStatus.AwaitingUpload, true)]
    [InlineData(PseudonymizationJobStatus.Queued, true)]
    [InlineData(PseudonymizationJobStatus.Running, true)]
    [InlineData(PseudonymizationJobStatus.Stalled, true)]
    [InlineData(PseudonymizationJobStatus.Completed, false)]
    [InlineData(PseudonymizationJobStatus.Failed, false)]
    [InlineData(PseudonymizationJobStatus.Cancelled, false)]
    public async Task MarkRunningAsync_ShouldApplyUnlessTheJobIsCancelledCompletedOrFailed(
        PseudonymizationJobStatus status,
        bool shouldApply
    )
    {
        var job = await SeedAsync(status);
        var sut = new PseudonymizationJobRepository(ContextFactory);

        var applied = await sut.MarkRunningAsync(job.Id, TestContext.Current.CancellationToken);

        applied.Should().Be(shouldApply);
        (await StatusOfAsync(job.Id))
            .Should()
            .Be(shouldApply ? PseudonymizationJobStatus.Running : status);
    }

    [Theory]
    [InlineData(PseudonymizationJobStatus.AwaitingUpload)]
    [InlineData(PseudonymizationJobStatus.Queued)]
    [InlineData(PseudonymizationJobStatus.Completed)]
    [InlineData(PseudonymizationJobStatus.Failed)]
    [InlineData(PseudonymizationJobStatus.Cancelled)]
    [InlineData(PseudonymizationJobStatus.Stalled)]
    public async Task MarkStalledAsync_WithAStaleJobThatIsNotRunning_ShouldLeaveItAlone(
        PseudonymizationJobStatus status
    )
    {
        // The case the condition exists for: found as a stale Running job, then completed or
        // cancelled - or marked by another replica's watchdog - before this write.
        var job = await SeedAsync(status, DateTimeOffset.UtcNow.AddMinutes(-20));
        var sut = new PseudonymizationJobRepository(ContextFactory);

        var applied = await sut.MarkStalledAsync(
            job.Id,
            TimeSpan.FromMinutes(10),
            "stalled",
            TestContext.Current.CancellationToken
        );

        applied.Should().BeFalse();
        (await StatusOfAsync(job.Id)).Should().Be(status);
    }

    [Fact]
    public async Task MarkStalledAsync_WithARunningJobThatHasSinceProgressed_ShouldLeaveItAlone()
    {
        var job = await SeedAsync(
            PseudonymizationJobStatus.Running,
            DateTimeOffset.UtcNow.AddSeconds(-5)
        );
        var sut = new PseudonymizationJobRepository(ContextFactory);

        var applied = await sut.MarkStalledAsync(
            job.Id,
            TimeSpan.FromMinutes(10),
            "stalled",
            TestContext.Current.CancellationToken
        );

        applied.Should().BeFalse();
        (await StatusOfAsync(job.Id)).Should().Be(PseudonymizationJobStatus.Running);
    }

    [Fact]
    public async Task MarkStalledAsync_WithAStaleRunningJob_ShouldMarkItStalledExactlyOnce()
    {
        // Every replica runs the watchdog: the second one to get here must find nothing to do.
        var job = await SeedAsync(
            PseudonymizationJobStatus.Running,
            DateTimeOffset.UtcNow.AddMinutes(-20)
        );
        var sut = new PseudonymizationJobRepository(ContextFactory);

        var first = await sut.MarkStalledAsync(
            job.Id,
            TimeSpan.FromMinutes(10),
            "stalled",
            TestContext.Current.CancellationToken
        );
        var second = await sut.MarkStalledAsync(
            job.Id,
            TimeSpan.FromMinutes(10),
            "stalled",
            TestContext.Current.CancellationToken
        );

        first.Should().BeTrue();
        second.Should().BeFalse();
        var stored = await InMemoryPseudonymContext
            .PseudonymizationJobs.AsNoTracking()
            .SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        stored.Status.Should().Be(PseudonymizationJobStatus.Stalled);
        stored.ErrorMessage.Should().Be("stalled");
    }

    [Theory]
    [InlineData(PseudonymizationJobStatus.AwaitingUpload, true)]
    [InlineData(PseudonymizationJobStatus.Queued, true)]
    [InlineData(PseudonymizationJobStatus.Running, true)]
    [InlineData(PseudonymizationJobStatus.Completed, false)]
    [InlineData(PseudonymizationJobStatus.Failed, false)]
    [InlineData(PseudonymizationJobStatus.Cancelled, false)]
    [InlineData(PseudonymizationJobStatus.Stalled, false)]
    public async Task MarkCancelledAsync_ShouldOnlyApplyToAJobThatHasNotFinished(
        PseudonymizationJobStatus status,
        bool shouldApply
    )
    {
        var job = await SeedAsync(status);
        var sut = new PseudonymizationJobRepository(ContextFactory);

        var applied = await sut.MarkCancelledAsync(job.Id, TestContext.Current.CancellationToken);

        applied.Should().Be(shouldApply);
        (await StatusOfAsync(job.Id))
            .Should()
            .Be(shouldApply ? PseudonymizationJobStatus.Cancelled : status);
    }

    [Theory]
    [InlineData(PseudonymizationJobStatus.Running, true)]
    [InlineData(PseudonymizationJobStatus.AwaitingUpload, false)]
    [InlineData(PseudonymizationJobStatus.Queued, false)]
    [InlineData(PseudonymizationJobStatus.Completed, false)]
    [InlineData(PseudonymizationJobStatus.Failed, false)]
    [InlineData(PseudonymizationJobStatus.Cancelled, false)]
    [InlineData(PseudonymizationJobStatus.Stalled, false)]
    public async Task CompleteAsync_ShouldOnlyApplyToARunningJob(
        PseudonymizationJobStatus status,
        bool shouldApply
    )
    {
        var job = await SeedAsync(status);
        var sut = new PseudonymizationJobRepository(ContextFactory);

        var applied = await sut.CompleteAsync(
            job.Id,
            "csv-jobs/output.csv",
            10,
            TestContext.Current.CancellationToken
        );

        applied.Should().Be(shouldApply);
        (await StatusOfAsync(job.Id))
            .Should()
            .Be(shouldApply ? PseudonymizationJobStatus.Completed : status);
    }
}
