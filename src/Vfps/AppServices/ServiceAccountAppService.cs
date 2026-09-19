using System.Security.Claims;
using System.Text.RegularExpressions;
using EntityFramework.Exceptions.Common;
using Vfps.Authorization;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <inheritdoc cref="IServiceAccountAppService"/>
public partial class ServiceAccountAppService(
    IServiceAccountRepository serviceAccountRepository,
    INamespaceAccessGrantRepository grantRepository,
    INamespacePermissionChecker permissionChecker,
    INamespaceAccessGrantCache grantCache,
    IAccessTokenCache tokenCache,
    ILogger<ServiceAccountAppService> logger
) : IServiceAccountAppService
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}[a-z0-9]$")]
    private static partial Regex ServiceAccountNamePattern { get; }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ServiceAccount>> GetAllAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureAdmin(user, "list service accounts");
        return await serviceAccountRepository.GetAllAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ServiceAccount?> FindAsync(
        string name,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureAdmin(user, "view a service account");
        return await serviceAccountRepository.FindAsync(name, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ServiceAccount> CreateAsync(
        string name,
        string? description,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureAdmin(user, "create a service account");

        var normalizedName = NormalizeName(name);
        var now = DateTimeOffset.UtcNow;
        var account = new ServiceAccount
        {
            Name = normalizedName,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            CreatedBy = user.GetSubject(),
            CreatedAt = now,
            LastUpdatedAt = now,
        };

        try
        {
            await serviceAccountRepository.CreateAsync(account, cancellationToken);
        }
        catch (UniqueConstraintException)
        {
            throw new ServiceAccountAlreadyExistsException(normalizedName);
        }

        logger.LogInformation(
            "Service account {ServiceAccountName} created by {CreatedBy}.",
            normalizedName,
            account.CreatedBy
        );

        return account;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(
        string name,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureAdmin(user, "delete a service account");

        var account =
            await serviceAccountRepository.FindAsync(name, cancellationToken)
            ?? throw new ServiceAccountNotFoundException(name);

        // Grants first: if this call dies between the two writes, the account still exists and an
        // admin can retry, whereas the other order would leave grants naming an account nobody
        // can see any more - and that a re-created account of the same name would inherit.
        var deletedGrants = await grantRepository.DeleteByGranteeAsync(
            GranteeType.ServiceAccount,
            account.Name,
            cancellationToken
        );

        // Tokens go with it through the foreign key's cascade - see PseudonymContext.
        await serviceAccountRepository.DeleteAsync(account.Name, cancellationToken);

        grantCache.Invalidate();
        tokenCache.Invalidate();

        logger.LogInformation(
            "Service account {ServiceAccountName} deleted by {DeletedBy}, along with {GrantCount} access grant(s) and every token issued for it.",
            account.Name,
            user.GetSubject(),
            deletedGrants
        );
    }

    private void EnsureAdmin(ClaimsPrincipal user, string action)
    {
        if (!permissionChecker.IsAdmin(user))
        {
            throw new ForbiddenException($"Admin access is required to {action}.");
        }
    }

    private static string NormalizeName(string name)
    {
        var normalized = name?.Trim().ToLowerInvariant() ?? string.Empty;

        if (normalized.Length == 0)
        {
            throw new ArgumentException("A service account needs a name.", nameof(name));
        }

        if (!ServiceAccountNamePattern.IsMatch(normalized))
        {
            throw new ArgumentException(
                $"'{normalized}' is not a valid service account name. Use 2-64 characters of "
                    + "lower-case letters, digits and hyphens, starting and ending with a letter "
                    + "or digit.",
                nameof(name)
            );
        }

        return normalized;
    }
}
