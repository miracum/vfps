using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;

namespace Vfps.Data;

/// <inheritdoc/>
public class NamespaceAccessGrantRepository(IDbContextFactory<PseudonymContext> contextFactory)
    : INamespaceAccessGrantRepository
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<NamespaceAccessGrant>> GetAllAsync(
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .NamespaceAccessGrants.AsNoTracking()
            .OrderBy(g => g.NamespaceName)
            .ThenBy(g => g.GranteeType)
            .ThenBy(g => g.Grantee)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<NamespaceAccessGrant?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .NamespaceAccessGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<NamespaceAccessGrant?> FindByGranteeAsync(
        string? namespaceName,
        GranteeType granteeType,
        string grantee,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .NamespaceAccessGrants.AsNoTracking()
            .FirstOrDefaultAsync(
                g =>
                    g.NamespaceName == namespaceName
                    && g.GranteeType == granteeType
                    && g.Grantee == grantee,
                cancellationToken
            );
    }

    /// <inheritdoc/>
    public async Task<NamespaceAccessGrant> CreateAsync(
        NamespaceAccessGrant grant,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Add(grant);
        await context.SaveChangesAsync(cancellationToken);
        return grant;
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(NamespaceAccessGrant grant, CancellationToken cancellationToken)
    {
        // Targeted column updates rather than attaching the whole entity: the caller's instance
        // came back from a no-tracking read, and only the three permission flags are editable
        // once a grant exists (the namespace and grantee identify it - change either and it's a
        // different grant).
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context
            .NamespaceAccessGrants.Where(g => g.Id == grant.Id)
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(g => g.CanRead, grant.CanRead)
                        .SetProperty(g => g.CanWrite, grant.CanWrite)
                        .SetProperty(g => g.CanReverseLookup, grant.CanReverseLookup)
                        .SetProperty(g => g.LastUpdatedAt, grant.LastUpdatedAt),
                cancellationToken
            );
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context
            .NamespaceAccessGrants.Where(g => g.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteByGranteeAsync(
        GranteeType granteeType,
        string grantee,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .NamespaceAccessGrants.Where(g => g.GranteeType == granteeType && g.Grantee == grantee)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
