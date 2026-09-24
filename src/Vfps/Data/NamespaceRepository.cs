using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;

namespace Vfps.Data;

public class NamespaceRepository(IDbContextFactory<PseudonymContext> contextFactory)
    : INamespaceRepository
{
    /// <inheritdoc/>
    public async Task<Namespace> CreateAsync(
        Namespace @namespace,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Add(@namespace);
        await context.SaveChangesAsync(cancellationToken);
        return @namespace;
    }

    /// <inheritdoc/>
    public async Task<Namespace?> FindAsync(
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .Namespaces.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Name == namespaceName, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Namespace>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Namespaces.AsNoTracking().ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Namespace>> ListChildrenAsync(
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .Namespaces.AsNoTracking()
            .Where(n => n.ParentName == namespaceName)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> HasChildrenAsync(
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Namespaces.AnyAsync(
            n => n.ParentName == namespaceName,
            cancellationToken
        );
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string namespaceName, CancellationToken cancellationToken)
    {
        // A direct bulk delete, not a load-then-Remove-then-SaveChanges round trip - the
        // database's own ON DELETE CASCADE foreign key constraint (see the Pseudonym/Namespace
        // relationship configuration) takes care of the contained pseudonyms without EF ever
        // needing to load or track them.
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context
            .Namespaces.Where(n => n.Name == namespaceName)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
