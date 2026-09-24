using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;

namespace Vfps.Data;

/// <inheritdoc/>
public class ServiceAccountRepository(IDbContextFactory<PseudonymContext> contextFactory)
    : IServiceAccountRepository
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<ServiceAccount>> GetAllAsync(
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .ServiceAccounts.AsNoTracking()
            .OrderBy(a => a.Name)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ServiceAccount?> FindAsync(string name, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .ServiceAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name == name, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ServiceAccount> CreateAsync(
        ServiceAccount account,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Add(account);
        await context.SaveChangesAsync(cancellationToken);
        return account;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context
            .ServiceAccounts.Where(a => a.Name == name)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
