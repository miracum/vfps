using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;

namespace Vfps.Data;

public class NamespaceRepository(PseudonymContext context) : INamespaceRepository
{
    /// <inheritdoc/>
    public async Task<Namespace> CreateAsync(
        Namespace @namespace,
        CancellationToken cancellationToken
    )
    {
        context.Add(@namespace);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            // Blazor Server keeps a single scoped PseudonymContext alive for a whole circuit
            // (unlike gRPC, where each call gets a fresh request-scoped context), so a tracked
            // entity from this call would otherwise still be present the next time this same
            // context instance is used - e.g. a second Create attempt with the same name would
            // conflict with this now-stale tracked instance before the query even reaches the
            // database. This must run on the failure path too (hence `finally`, not just after
            // a successful save) - a duplicate-name attempt fails SaveChangesAsync, but leaves
            // the entity tracked as "Added" regardless, so the *next* Create call (even for a
            // different, genuinely new name) would try to re-insert that stale failed entity
            // alongside the new one and either violate the same unique constraint again under
            // the new call's name, or hit this exact "already being tracked" conflict itself.
            context.ChangeTracker.Clear();
        }

        return @namespace;
    }

    /// <inheritdoc/>
    public async Task<Namespace?> FindAsync(
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        // Explicit no-tracking query rather than DbSet.FindAsync, which would otherwise
        // check (and can return) a stale locally-tracked instance from an earlier operation
        // on this same context - a real risk given Blazor Server's circuit-scoped lifetime.
        return await context
            .Namespaces.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Name == namespaceName, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Namespace>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await context.Namespaces.AsNoTracking().ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string namespaceName, CancellationToken cancellationToken)
    {
        // A direct bulk delete, not a load-then-Remove-then-SaveChanges round trip - the
        // database's own ON DELETE CASCADE foreign key constraint (see the Pseudonym/Namespace
        // relationship configuration) takes care of the contained pseudonyms without EF ever
        // needing to load or track them.
        await context
            .Namespaces.Where(n => n.Name == namespaceName)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
