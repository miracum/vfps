using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;

namespace Vfps.Data;

/// <inheritdoc/>
public class PseudonymizationJobRepository(PseudonymContext context)
    : IPseudonymizationJobRepository
{
    /// <inheritdoc/>
    public async Task<PseudonymizationJob> CreateAsync(
        PseudonymizationJob job,
        CancellationToken cancellationToken
    )
    {
        context.Add(job);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            // See NamespaceRepository.CreateAsync for why this must run on the failure path
            // too (hence `finally`), not just after a successful save.
            context.ChangeTracker.Clear();
        }

        return job;
    }

    /// <inheritdoc/>
    public async Task<PseudonymizationJob?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        return await context
            .PseudonymizationJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PseudonymizationJob>> ListAsync(
        string? createdBy,
        CancellationToken cancellationToken
    )
    {
        var query = context.PseudonymizationJobs.AsNoTracking();
        if (createdBy is not null)
        {
            query = query.Where(j => j.CreatedBy == createdBy);
        }

        return await query.OrderByDescending(j => j.CreatedAt).ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Guid>> FindStalledRunningJobIdsAsync(
        TimeSpan staleAfter,
        CancellationToken cancellationToken
    )
    {
        var threshold = DateTimeOffset.UtcNow - staleAfter;

        return await context
            .PseudonymizationJobs.AsNoTracking()
            .Where(j =>
                j.Status == PseudonymizationJobStatus.Running && j.LastUpdatedAt < threshold
            )
            .Select(j => j.Id)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateProgressAsync(
        Guid id,
        long bytesProcessed,
        long rowsProcessed,
        int badDataRowCount,
        CancellationToken cancellationToken
    )
    {
        await context
            .PseudonymizationJobs.Where(j => j.Id == id)
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(j => j.BytesProcessed, bytesProcessed)
                        .SetProperty(j => j.RowsProcessed, rowsProcessed)
                        .SetProperty(j => j.BadDataRowCount, badDataRowCount)
                        .SetProperty(j => j.LastUpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken
            );
    }

    /// <inheritdoc/>
    public async Task UpdateStatusAsync(
        Guid id,
        PseudonymizationJobStatus status,
        string? errorMessage,
        CancellationToken cancellationToken
    )
    {
        await context
            .PseudonymizationJobs.Where(j => j.Id == id)
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(j => j.Status, status)
                        .SetProperty(j => j.ErrorMessage, errorMessage)
                        .SetProperty(j => j.LastUpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken
            );
    }

    /// <inheritdoc/>
    public async Task MarkQueuedAsync(Guid id, long totalBytes, CancellationToken cancellationToken)
    {
        await context
            .PseudonymizationJobs.Where(j => j.Id == id)
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(j => j.Status, PseudonymizationJobStatus.Queued)
                        .SetProperty(j => j.TotalBytes, totalBytes)
                        .SetProperty(j => j.LastUpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken
            );
    }

    /// <inheritdoc/>
    public async Task SetHangfireJobIdAsync(
        Guid id,
        string hangfireJobId,
        CancellationToken cancellationToken
    )
    {
        await context
            .PseudonymizationJobs.Where(j => j.Id == id)
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(j => j.HangfireJobId, hangfireJobId)
                        .SetProperty(j => j.LastUpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken
            );
    }

    /// <inheritdoc/>
    public async Task CompleteAsync(
        Guid id,
        string outputObjectKey,
        long rowsProcessed,
        CancellationToken cancellationToken
    )
    {
        await context
            .PseudonymizationJobs.Where(j => j.Id == id)
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(j => j.Status, PseudonymizationJobStatus.Completed)
                        .SetProperty(j => j.OutputObjectKey, outputObjectKey)
                        .SetProperty(j => j.RowsProcessed, rowsProcessed)
                        .SetProperty(j => j.LastUpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken
            );
    }

    /// <inheritdoc/>
    public async Task<int> DeleteFinishedAsync(
        string? createdBy,
        CancellationToken cancellationToken
    )
    {
        var query = context.PseudonymizationJobs.Where(j =>
            j.Status == PseudonymizationJobStatus.Completed
            || j.Status == PseudonymizationJobStatus.Failed
            || j.Status == PseudonymizationJobStatus.Cancelled
            || j.Status == PseudonymizationJobStatus.Stalled
        );

        if (createdBy is not null)
        {
            query = query.Where(j => j.CreatedBy == createdBy);
        }

        return await query.ExecuteDeleteAsync(cancellationToken);
    }
}
