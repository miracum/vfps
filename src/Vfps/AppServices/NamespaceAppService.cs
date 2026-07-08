using EntityFramework.Exceptions.Common;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <inheritdoc cref="INamespaceAppService"/>
public class NamespaceAppService(INamespaceRepository namespaceRepository) : INamespaceAppService
{
    /// <inheritdoc/>
    public async Task<Namespace> CreateAsync(
        Namespace namespaceToCreate,
        CancellationToken cancellationToken
    )
    {
        if (namespaceToCreate.PseudonymLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(namespaceToCreate),
                "Pseudonym length must be larger than 0."
            );
        }

        var now = DateTimeOffset.UtcNow;
        namespaceToCreate.CreatedAt = now;
        namespaceToCreate.LastUpdatedAt = now;

        try
        {
            await namespaceRepository.CreateAsync(namespaceToCreate, cancellationToken);
        }
        catch (UniqueConstraintException)
        {
            throw new NamespaceAlreadyExistsException(namespaceToCreate.Name);
        }

        return namespaceToCreate;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Namespace>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await namespaceRepository.GetAllAsync(cancellationToken);
    }
}
