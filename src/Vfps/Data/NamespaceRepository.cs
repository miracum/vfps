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
        await context.SaveChangesAsync(cancellationToken);

        // Blazor Server keeps a single scoped PseudonymContext alive for a whole circuit
        // (unlike gRPC, where each call gets a fresh request-scoped context), so a tracked
        // entity from this call would otherwise still be present the next time this same
        // context instance is used - e.g. a second Create attempt with the same name would
        // conflict with this now-stale tracked instance before the query even reaches the
        // database. Clearing here keeps this repository correct regardless of caller lifetime.
        context.ChangeTracker.Clear();

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
}
