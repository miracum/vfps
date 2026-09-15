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

        var sut = new PseudonymizationJobRepository(InMemoryPseudonymContext);

        var stillActive = await sut.UpdateProgressUnlessCancelledAsync(
            job.Id,
            bytesProcessed: 4096,
            rowsProcessed: 200,
            badDataRowCount: 3,
            missingValueCount: 7,
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

        var sut = new PseudonymizationJobRepository(InMemoryPseudonymContext);

        var stillActive = await sut.UpdateProgressUnlessCancelledAsync(
            job.Id,
            bytesProcessed: 4096,
            rowsProcessed: 200,
            badDataRowCount: 0,
            missingValueCount: 0,
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

        var sut = new PseudonymizationJobRepository(InMemoryPseudonymContext);

        var stillActive = await sut.UpdateProgressUnlessCancelledAsync(
            job.Id,
            bytesProcessed: 1,
            rowsProcessed: 1,
            badDataRowCount: 0,
            missingValueCount: 0,
            TestContext.Current.CancellationToken
        );

        stillActive.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateProgressUnlessCancelledAsync_WithUnknownJob_ShouldReportInactive()
    {
        var sut = new PseudonymizationJobRepository(InMemoryPseudonymContext);

        var stillActive = await sut.UpdateProgressUnlessCancelledAsync(
            Guid.NewGuid(),
            bytesProcessed: 1,
            rowsProcessed: 1,
            badDataRowCount: 0,
            missingValueCount: 0,
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
