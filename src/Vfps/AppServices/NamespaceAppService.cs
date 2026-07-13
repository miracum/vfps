using System.Security.Claims;
using EntityFramework.Exceptions.Common;
using Vfps.Authorization;
using Vfps.Data;
using Vfps.Data.Models;
using Vfps.PseudonymGenerators;

namespace Vfps.AppServices;

/// <inheritdoc cref="INamespaceAppService"/>
public class NamespaceAppService(
    INamespaceRepository namespaceRepository,
    INamespacePermissionChecker permissionChecker,
    PseudonymizationMethodsLookup methodsLookup
) : INamespaceAppService
{
    /// <inheritdoc/>
    public async Task<Namespace> CreateAsync(
        Namespace namespaceToCreate,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        // Namespace creation has no existing namespace to scope a read/write grant to, so it's
        // gated by admin access only.
        if (!permissionChecker.IsAdmin(user))
        {
            throw new ForbiddenException("Creating a namespace requires admin access.");
        }

        if (namespaceToCreate.PseudonymLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(namespaceToCreate),
                "Pseudonym length must be larger than 0."
            );
        }

        // Some generation methods (UUIDs, SHA-256 hex) have no configurable length at all - catch
        // a mismatch here, at namespace creation, rather than leaving it to fail lazily on every
        // subsequent pseudonym creation in this namespace.
        var fixedLength = methodsLookup.GetFixedPseudonymLength(
            namespaceToCreate.PseudonymGenerationMethod
        );
        if (fixedLength is not null && namespaceToCreate.PseudonymLength != fixedLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(namespaceToCreate),
                $"The '{namespaceToCreate.PseudonymGenerationMethod}' pseudonym generation "
                    + $"method requires a pseudonym length of exactly {fixedLength}."
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
    public async Task<IReadOnlyList<Namespace>> GetAllAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var namespaces = await namespaceRepository.GetAllAsync(cancellationToken);

        // No single target namespace to gate GetAll on - filter the result set per-row against
        // the caller's resolved rules instead.
        return [.. namespaces.Where(n => permissionChecker.HasReadAccess(user, n.Name))];
    }
}
