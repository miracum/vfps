using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;

namespace Vfps.Data;

/// <inheritdoc/>
public class AccessTokenRepository(PseudonymContext context) : IAccessTokenRepository
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<AccessToken>> GetAllUnrevokedAsync(
        CancellationToken cancellationToken
    ) =>
        await context
            .AccessTokens.AsNoTracking()
            .Where(t => t.RevokedAt == null)
            .ToListAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AccessToken>> GetAllAsync(
        CancellationToken cancellationToken
    ) =>
        await context
            .AccessTokens.AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AccessToken>> GetBySubjectAsync(
        string subject,
        CancellationToken cancellationToken
    ) =>
        await context
            .AccessTokens.AsNoTracking()
            .Where(t => t.Subject == subject)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AccessToken>> GetByServiceAccountAsync(
        string serviceAccountName,
        CancellationToken cancellationToken
    ) =>
        await context
            .AccessTokens.AsNoTracking()
            .Where(t => t.ServiceAccountName == serviceAccountName)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<AccessToken?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await context
            .AccessTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    /// <inheritdoc/>
    public async Task<AccessToken> CreateAsync(
        AccessToken token,
        CancellationToken cancellationToken
    )
    {
        context.Add(token);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            // See NamespaceRepository.CreateAsync for why this runs on the failure path too.
            context.ChangeTracker.Clear();
        }

        return token;
    }

    /// <inheritdoc/>
    public async Task RevokeAsync(
        Guid id,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken
    )
    {
        // Filtered on RevokedAt == null so re-revoking doesn't move the timestamp: when a token
        // stopped working is an audit fact, and the second click shouldn't rewrite it.
        await context
            .AccessTokens.Where(t => t.Id == id && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(t => t.RevokedAt, revokedAt)
                        .SetProperty(t => t.LastUpdatedAt, revokedAt),
                cancellationToken
            );
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await context.AccessTokens.Where(t => t.Id == id).ExecuteDeleteAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task UpdateLastUsedAsync(
        IReadOnlyDictionary<Guid, DateTimeOffset> lastUsedByTokenId,
        CancellationToken cancellationToken
    )
    {
        // One statement per token rather than one CASE expression over all of them: a flush
        // carries at most one entry per token actually used in the interval, which in every
        // realistic deployment is a handful. LastUpdatedAt is deliberately left alone - this
        // column says nothing about the token's configuration.
        foreach (var (id, lastUsedAt) in lastUsedByTokenId)
        {
            await context
                .AccessTokens.Where(t =>
                    t.Id == id && (t.LastUsedAt == null || t.LastUsedAt < lastUsedAt)
                )
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(t => t.LastUsedAt, lastUsedAt),
                    cancellationToken
                );
        }
    }
}
