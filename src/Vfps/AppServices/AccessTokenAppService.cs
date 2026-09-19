using System.Security.Claims;
using Microsoft.Extensions.Options;
using Vfps.Authorization;
using Vfps.Config;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <inheritdoc cref="IAccessTokenAppService"/>
public class AccessTokenAppService(
    IAccessTokenRepository tokenRepository,
    IServiceAccountRepository serviceAccountRepository,
    INamespacePermissionChecker permissionChecker,
    IAccessTokenCache tokenCache,
    IOptions<AuthorizationConfig> options,
    ILogger<AccessTokenAppService> logger
) : IAccessTokenAppService
{
    private AuthorizationConfig Config => options.Value;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AccessToken>> GetMineAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureEnabled();
        return await tokenRepository.GetBySubjectAsync(RequireSubject(user), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AccessToken>> GetAllAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureEnabled();
        EnsureAdmin(user, "list every access token");
        return await tokenRepository.GetAllAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AccessToken>> GetForServiceAccountAsync(
        string serviceAccountName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureEnabled();
        EnsureAdmin(user, "list a service account's tokens");
        return await tokenRepository.GetByServiceAccountAsync(
            serviceAccountName,
            cancellationToken
        );
    }

    /// <inheritdoc/>
    public async Task<IssuedAccessToken> CreatePersonalAsync(
        string name,
        TimeSpan lifetime,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureEnabled();
        EnsureCanMintTokens(user);

        var subject = RequireSubject(user);
        var label = NormalizeLabel(name);
        var expiresAt = ResolveExpiry(lifetime);

        var (token, secret) = NewToken(AccessTokenType.Personal, label, expiresAt, subject);
        token.Subject = subject;
        token.Username = user.FindFirstValue("preferred_username");

        // Only a verified address, matching what a permission check would honour for this same
        // user in a browser session - GetEmail() returns null for an unverified one.
        token.Email = user.GetEmail()?.ToLowerInvariant();
        token.Roles = [.. user.FindAll(Config.RoleClaimType).Select(claim => claim.Value)];

        await PersistAsync(token, cancellationToken);

        logger.LogInformation(
            "Personal access token {TokenId} ({TokenName}) created for {Subject}, expiring {ExpiresAt}.",
            token.TokenId,
            token.Name,
            subject,
            token.ExpiresAt
        );

        return new IssuedAccessToken(token, secret);
    }

    /// <inheritdoc/>
    public async Task<IssuedAccessToken> CreateForServiceAccountAsync(
        string serviceAccountName,
        string name,
        TimeSpan lifetime,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureEnabled();
        EnsureAdmin(user, "create a service account token");
        EnsureCanMintTokens(user);

        var account =
            await serviceAccountRepository.FindAsync(serviceAccountName, cancellationToken)
            ?? throw new ServiceAccountNotFoundException(serviceAccountName);

        var label = NormalizeLabel(name);
        var expiresAt = ResolveExpiry(lifetime);

        var (token, secret) = NewToken(
            AccessTokenType.ServiceAccount,
            label,
            expiresAt,
            user.GetSubject()
        );
        token.ServiceAccountName = account.Name;

        await PersistAsync(token, cancellationToken);

        logger.LogInformation(
            "Service account token {TokenId} ({TokenName}) created for {ServiceAccountName} by {CreatedBy}, expiring {ExpiresAt}.",
            token.TokenId,
            token.Name,
            account.Name,
            token.CreatedBy,
            token.ExpiresAt
        );

        return new IssuedAccessToken(token, secret);
    }

    /// <inheritdoc/>
    public async Task RevokeAsync(
        Guid id,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureEnabled();

        var token =
            await tokenRepository.FindAsync(id, cancellationToken)
            ?? throw new AccessTokenNotFoundException(id);

        EnsureMayManage(token, user, "revoke");

        await tokenRepository.RevokeAsync(id, DateTimeOffset.UtcNow, cancellationToken);
        tokenCache.Invalidate();

        logger.LogInformation(
            "Access token {TokenId} revoked by {RevokedBy}.",
            token.TokenId,
            user.GetSubject()
        );
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(
        Guid id,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        EnsureEnabled();

        var token =
            await tokenRepository.FindAsync(id, cancellationToken)
            ?? throw new AccessTokenNotFoundException(id);

        EnsureMayManage(token, user, "delete");

        await tokenRepository.DeleteAsync(id, cancellationToken);

        // Invalidated even though a deleted row can't authenticate anything once the snapshot is
        // reloaded: without this, a *live* token deleted rather than revoked would keep working
        // on this replica until the snapshot aged out.
        tokenCache.Invalidate();

        logger.LogInformation(
            "Access token {TokenId} deleted by {DeletedBy}.",
            token.TokenId,
            user.GetSubject()
        );
    }

    /// <summary>
    /// The row to store and the string to show the creator, produced together: the secret exists
    /// only as a local here and in the hash on the entity, so there is no point at which a
    /// caller could read it back off the token afterwards.
    /// </summary>
    private static (AccessToken Token, string Secret) NewToken(
        AccessTokenType tokenType,
        string name,
        DateTimeOffset expiresAt,
        string createdBy
    )
    {
        var generated = AccessTokenSecret.Generate(tokenType);
        var now = DateTimeOffset.UtcNow;

        var token = new AccessToken
        {
            Id = Guid.NewGuid(),
            TokenType = tokenType,
            TokenId = generated.TokenId,
            TokenHash = generated.Hash,
            Name = name,
            ExpiresAt = expiresAt,
            CreatedBy = createdBy,
            CreatedAt = now,
            LastUpdatedAt = now,
        };

        return (token, generated.Presented);
    }

    private async Task PersistAsync(AccessToken token, CancellationToken cancellationToken)
    {
        await tokenRepository.CreateAsync(token, cancellationToken);

        // So the new token works on this replica immediately, rather than only once the snapshot
        // next expires - the same immediacy an admin's own grant change gets.
        tokenCache.Invalidate();
    }

    private void EnsureEnabled()
    {
        if (!Config.IsEnabled)
        {
            throw new ForbiddenException(
                "Access tokens require authorization to be enabled - there is no identity to "
                    + "issue one for otherwise."
            );
        }

        if (!Config.AccessTokens.IsEnabled)
        {
            throw new ForbiddenException(
                "Access tokens are disabled. Set Authorization__AccessTokens__IsEnabled to true "
                    + "to allow them."
            );
        }
    }

    private void EnsureAdmin(ClaimsPrincipal user, string action)
    {
        if (!permissionChecker.IsAdmin(user))
        {
            throw new ForbiddenException($"Admin access is required to {action}.");
        }
    }

    /// <summary>
    /// A token may never mint another token. Without this, a leaked personal access token could
    /// issue itself a fresh one before the original expired, and expiry - the bound that works
    /// even when nobody has noticed the leak - would stop meaning anything.
    /// </summary>
    private static void EnsureCanMintTokens(ClaimsPrincipal user)
    {
        if (user.IsAccessTokenPrincipal())
        {
            throw new ForbiddenException(
                "An access token cannot create another access token. Sign in to the admin UI to "
                    + "create one."
            );
        }
    }

    /// <summary>
    /// Who may revoke or delete a token: an admin, or - for a personal one - the user it belongs
    /// to. A service-account token has no owner, so it is admins only.
    /// </summary>
    private void EnsureMayManage(AccessToken token, ClaimsPrincipal user, string action)
    {
        if (permissionChecker.IsAdmin(user))
        {
            return;
        }

        var isOwnPersonalToken =
            token.TokenType == AccessTokenType.Personal
            && token.Subject is not null
            && string.Equals(token.Subject, user.GetSubject(), StringComparison.Ordinal);

        if (!isOwnPersonalToken)
        {
            throw new ForbiddenException($"Only an admin can {action} this access token.");
        }
    }

    private static string RequireSubject(ClaimsPrincipal user)
    {
        var subject = user.GetSubject();

        // "anonymous" is what GetSubject() reports when there is no "sub" claim at all. A token
        // issued for it would be a credential belonging to nobody, listed for everybody.
        if (subject == "anonymous")
        {
            throw new ForbiddenException(
                "Only a signed-in user can manage personal access tokens."
            );
        }

        return subject;
    }

    private static string NormalizeLabel(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new ArgumentException(
                "An access token needs a name so it can be told apart from the others.",
                nameof(name)
            );
        }

        return trimmed.Length > 128 ? trimmed[..128] : trimmed;
    }

    private DateTimeOffset ResolveExpiry(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An access token's lifetime must be positive - a token always expires.",
                nameof(lifetime)
            );
        }

        var maximum = Config.AccessTokens.MaximumLifetime;
        if (maximum > TimeSpan.Zero && lifetime > maximum)
        {
            throw new ArgumentException(
                $"An access token may live at most {maximum.TotalDays:0.##} days.",
                nameof(lifetime)
            );
        }

        return DateTimeOffset.UtcNow.Add(lifetime);
    }
}
