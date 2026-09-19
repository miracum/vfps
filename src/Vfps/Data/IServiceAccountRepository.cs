using Vfps.Data.Models;

namespace Vfps.Data;

/// <summary>
/// Store and retrieve <see cref="ServiceAccount"/>s - the named non-human principals namespace
/// access can be granted to.
/// </summary>
public interface IServiceAccountRepository
{
    /// <summary>
    /// Every service account. Unpaginated, for the same low-cardinality reason as
    /// <see cref="INamespaceRepository.GetAllAsync"/>.
    /// </summary>
    Task<IReadOnlyList<ServiceAccount>> GetAllAsync(CancellationToken cancellationToken);

    Task<ServiceAccount?> FindAsync(string name, CancellationToken cancellationToken);

    Task<ServiceAccount> CreateAsync(ServiceAccount account, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an account and, by cascade, every token issued for it. A no-op if it doesn't
    /// exist. The account's access grants are *not* touched here - see
    /// <see cref="AppServices.IServiceAccountAppService.DeleteAsync"/>, which removes them in the
    /// same operation.
    /// </summary>
    Task DeleteAsync(string name, CancellationToken cancellationToken);
}
