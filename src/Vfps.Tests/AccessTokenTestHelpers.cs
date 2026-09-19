using Vfps.Data.Models;

namespace Vfps.Tests;

/// <summary>
/// A token source fixed at construction, standing in for the real <see cref="AccessTokenCache"/>
/// so authentication in tests doesn't need a database - the counterpart of
/// <see cref="StaticNamespaceAccessGrantCache"/>.
/// </summary>
internal sealed class StaticAccessTokenCache(params AccessToken[] tokens) : IAccessTokenCache
{
    public int InvalidateCallCount { get; private set; }

    public Task<AccessToken?> FindAsync(string tokenId, CancellationToken cancellationToken) =>
        Task.FromResult(
            tokens.FirstOrDefault(t =>
                t.RevokedAt is null && string.Equals(t.TokenId, tokenId, StringComparison.Ordinal)
            )
        );

    public void Invalidate() => InvalidateCallCount++;
}

/// <summary>Builders for the two kinds of access token.</summary>
internal static class Tokens
{
    /// <summary>
    /// A personal token and the secret it was minted with, so a test can present the real string
    /// rather than reaching past the hash.
    /// </summary>
    public static (AccessToken Token, string Secret) Personal(
        string subject = "user-subject",
        string? email = null,
        string[]? roles = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null
    )
    {
        var generated = AccessTokenSecret.Generate(AccessTokenType.Personal);
        var token = Build(AccessTokenType.Personal, generated, expiresAt, revokedAt);
        token.Subject = subject;
        token.Username = subject;
        token.Email = email;
        token.Roles = [.. roles ?? []];

        return (token, generated.Presented);
    }

    public static (AccessToken Token, string Secret) ForServiceAccount(
        string serviceAccountName,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null
    )
    {
        var generated = AccessTokenSecret.Generate(AccessTokenType.ServiceAccount);
        var token = Build(AccessTokenType.ServiceAccount, generated, expiresAt, revokedAt);
        token.ServiceAccountName = serviceAccountName;

        return (token, generated.Presented);
    }

    private static AccessToken Build(
        AccessTokenType tokenType,
        AccessTokenSecret.GeneratedToken generated,
        DateTimeOffset? expiresAt,
        DateTimeOffset? revokedAt
    )
    {
        var now = DateTimeOffset.UtcNow;

        return new AccessToken
        {
            Id = Guid.NewGuid(),
            TokenType = tokenType,
            TokenId = generated.TokenId,
            TokenHash = generated.Hash,
            Name = "test token",
            ExpiresAt = expiresAt ?? now.AddDays(30),
            RevokedAt = revokedAt,
            CreatedBy = "creator",
            CreatedAt = now,
            LastUpdatedAt = now,
        };
    }
}
