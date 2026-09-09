using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using EntityFramework.Exceptions.Common;
using Vfps.Authorization;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <inheritdoc cref="INamespaceAccessGrantAppService"/>
public class NamespaceAccessGrantAppService(
    INamespaceAccessGrantRepository grantRepository,
    INamespaceRepository namespaceRepository,
    INamespacePermissionChecker permissionChecker,
    INamespaceAccessGrantCache grantCache
) : INamespaceAccessGrantAppService
{
    private static readonly EmailAddressAttribute EmailValidator = new();

    /// <inheritdoc/>
    public async Task<IReadOnlyList<NamespaceAccessGrant>> GetAllAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureAdmin(user, "list access grants");
        return await grantRepository.GetAllAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<NamespaceAccessGrant> CreateAsync(
        string? namespaceName,
        GranteeType granteeType,
        string grantee,
        bool canRead,
        bool canWrite,
        bool canReverseLookup,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureAdmin(user, "create an access grant");

        var normalizedGrantee = Normalize(granteeType, grantee);

        // Only checked on create. UpdateAsync deliberately allows every flag to be cleared: the
        // UI persists each checkbox as it's toggled, so refusing the last one would turn a normal
        // edit into an error - and a grant that grants nothing is harmless and plainly visible in
        // the grid, where it can be deleted. Creating one, on the other hand, is only ever a slip.
        if (!canRead && !canWrite && !canReverseLookup)
        {
            throw new ArgumentException(
                "An access grant must grant at least one of read, write or reverse-lookup access.",
                nameof(canRead)
            );
        }

        if (
            namespaceName is not null
            && await namespaceRepository.FindAsync(namespaceName, cancellationToken) is null
        )
        {
            throw new NamespaceNotFoundException(namespaceName);
        }

        if (
            await grantRepository.FindByGranteeAsync(
                namespaceName,
                granteeType,
                normalizedGrantee,
                cancellationToken
            )
            is not null
        )
        {
            throw new NamespaceAccessGrantAlreadyExistsException(normalizedGrantee, namespaceName);
        }

        var now = DateTimeOffset.UtcNow;
        var grant = new NamespaceAccessGrant
        {
            NamespaceName = namespaceName,
            GranteeType = granteeType,
            Grantee = normalizedGrantee,
            CanRead = canRead,
            CanWrite = canWrite,
            CanReverseLookup = canReverseLookup,
            CreatedAt = now,
            LastUpdatedAt = now,
        };

        try
        {
            await grantRepository.CreateAsync(grant, cancellationToken);
        }
        catch (UniqueConstraintException)
        {
            // The FindByGranteeAsync check above lost a race with another admin - the partial
            // unique indexes in PseudonymContext are what actually enforce this.
            throw new NamespaceAccessGrantAlreadyExistsException(normalizedGrantee, namespaceName);
        }

        grantCache.Invalidate();
        return grant;
    }

    /// <inheritdoc/>
    public async Task<NamespaceAccessGrant> UpdateAsync(
        Guid id,
        bool canRead,
        bool canWrite,
        bool canReverseLookup,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureAdmin(user, "edit an access grant");

        var grant =
            await grantRepository.FindAsync(id, cancellationToken)
            ?? throw new NamespaceAccessGrantNotFoundException(id);

        grant.CanRead = canRead;
        grant.CanWrite = canWrite;
        grant.CanReverseLookup = canReverseLookup;
        grant.LastUpdatedAt = DateTimeOffset.UtcNow;

        await grantRepository.UpdateAsync(grant, cancellationToken);

        grantCache.Invalidate();
        return grant;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(
        Guid id,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureAdmin(user, "delete an access grant");

        if (await grantRepository.FindAsync(id, cancellationToken) is null)
        {
            throw new NamespaceAccessGrantNotFoundException(id);
        }

        await grantRepository.DeleteAsync(id, cancellationToken);

        grantCache.Invalidate();
    }

    private void EnsureAdmin(ClaimsPrincipal user, string action)
    {
        if (!permissionChecker.IsAdmin(user))
        {
            throw new ForbiddenException($"Admin access is required to {action}.");
        }
    }

    private static string Normalize(GranteeType granteeType, string grantee)
    {
        var trimmed = grantee?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException(
                "An access grant needs a role name or email address.",
                nameof(grantee)
            );
        }

        if (granteeType != GranteeType.Email)
        {
            // Role names are matched exactly as the IdP emits them, so they're stored as typed.
            return trimmed;
        }

        if (!EmailValidator.IsValid(trimmed))
        {
            throw new ArgumentException(
                $"'{trimmed}' is not a valid email address.",
                nameof(grantee)
            );
        }

        // Lower-cased on the way in so the unique indexes catch "User@Example.org" as a duplicate
        // of "user@example.org", and so matching against the "email" claim is a plain comparison.
        return trimmed.ToLowerInvariant();
    }
}
