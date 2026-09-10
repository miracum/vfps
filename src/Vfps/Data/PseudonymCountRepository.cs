using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;

namespace Vfps.Data;

/// <inheritdoc/>
public class PseudonymCountRepository(PseudonymContext context) : IPseudonymCountRepository
{
    /// <inheritdoc/>
    public async Task ReplaceAllAsync(
        IReadOnlyDictionary<string, long> counts,
        DateTimeOffset computedAt,
        CancellationToken cancellationToken
    )
    {
        // Merged in memory rather than pushed down as an INSERT ... ON CONFLICT, even though that
        // is exactly what this is. Raw SQL bypasses EF's value converters, and DateTimeOffset is
        // stored as a *string* on SQLite (see PseudonymContext) - a hand-written statement would
        // write a different format than the model reads back. The table is one row per namespace,
        // so reading it first costs nothing.
        //
        // Only the keys, and explicitly AsNoTracking, because entity state is then set explicitly
        // below rather than inferred by change detection. That keeps this correct no matter what
        // the context's ambient query tracking behaviour is - the unit tests configure NoTracking
        // globally while production leaves it tracking, and a silent no-op under one of the two is
        // exactly the bug this shape avoids.
        var existing = (
            await context
                .PseudonymCounts.AsNoTracking()
                .Select(row => row.NamespaceName)
                .ToListAsync(cancellationToken)
        ).ToHashSet(StringComparer.Ordinal);

        foreach (var (namespaceName, count) in counts)
        {
            var row = new PseudonymCount
            {
                NamespaceName = namespaceName,
                Count = count,
                ComputedAt = computedAt,
            };

            if (existing.Contains(namespaceName))
            {
                context.PseudonymCounts.Update(row);
            }
            else
            {
                context.PseudonymCounts.Add(row);
            }
        }

        // Wholesale replacement: whatever is no longer in the set has to go, or a namespace that
        // lost its last pseudonym would stay pinned at its final count forever.
        foreach (var staleName in existing.Where(name => !counts.ContainsKey(name)))
        {
            context.PseudonymCounts.Remove(new PseudonymCount { NamespaceName = staleName });
        }

        // One SaveChanges, so the whole replacement is a single transaction and a replica reading
        // concurrently never observes a half-written set.
        await context.SaveChangesAsync(cancellationToken);

        // The stubs above stay tracked as Unchanged once saved, which would collide with the next
        // call's stubs for the same keys. Detaching just this entity type leaves anything else the
        // caller has pending alone - unlike ChangeTracker.Clear().
        foreach (var entry in context.ChangeTracker.Entries<PseudonymCount>().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, long>> GetAllAsync(
        CancellationToken cancellationToken
    ) =>
        await context
            .PseudonymCounts.AsNoTracking()
            .ToDictionaryAsync(row => row.NamespaceName, row => row.Count, cancellationToken);
}
