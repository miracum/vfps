using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;

namespace Vfps.Data;

/// <inheritdoc/>
public class ServiceAccountRepository(PseudonymContext context) : IServiceAccountRepository
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<ServiceAccount>> GetAllAsync(
        CancellationToken cancellationToken
    ) =>
        await context
            .ServiceAccounts.AsNoTracking()
            .OrderBy(a => a.Name)
            .ToListAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<ServiceAccount?> FindAsync(
        string name,
        CancellationToken cancellationToken
    ) =>
        await context
            .ServiceAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name == name, cancellationToken);

    /// <inheritdoc/>
    public async Task<ServiceAccount> CreateAsync(
        ServiceAccount account,
        CancellationToken cancellationToken
    )
    {
        context.Add(account);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            // See NamespaceRepository.CreateAsync for why this runs on the failure path too.
            context.ChangeTracker.Clear();
        }

        return account;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string name, CancellationToken cancellationToken) =>
        await context
            .ServiceAccounts.Where(a => a.Name == name)
            .ExecuteDeleteAsync(cancellationToken);
}
