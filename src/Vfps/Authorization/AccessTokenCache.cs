using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.Authorization;

/// <inheritdoc/>
public sealed class AccessTokenCache(
    IAccessTokenRepository tokenRepository,
    IOptions<AuthorizationConfig> options
) : IAccessTokenCache, IDisposable
{
    // Structured exactly like NamespaceAccessGrantCache - see the comments there for why the
    // reload is serialized and why the snapshot is swapped as one immutable object.
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    private Snapshot? _snapshot;

    private TimeSpan CacheDuration => options.Value.GrantCacheDuration;

    /// <inheritdoc/>
    public async Task<AccessToken?> FindAsync(string tokenId, CancellationToken cancellationToken)
    {
        var tokens = await GetAllAsync(cancellationToken);
        return tokens.GetValueOrDefault(tokenId);
    }

    /// <inheritdoc/>
    public void Invalidate() => Volatile.Write(ref _snapshot, null);

    public void Dispose() => _reloadLock.Dispose();

    private async Task<IReadOnlyDictionary<string, AccessToken>> GetAllAsync(
        CancellationToken cancellationToken
    )
    {
        if (TryGetFresh(out var cached))
        {
            return cached;
        }

        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            if (TryGetFresh(out cached))
            {
                return cached;
            }

            var tokens = await tokenRepository.GetAllUnrevokedAsync(cancellationToken);

            // Ordinal: a token id is base64url, and case is significant in it.
            var byTokenId = tokens.ToDictionary(t => t.TokenId, StringComparer.Ordinal);

            Volatile.Write(ref _snapshot, new Snapshot(byTokenId, DateTimeOffset.UtcNow));
            return byTokenId;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private bool TryGetFresh(out IReadOnlyDictionary<string, AccessToken> tokens)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var duration = CacheDuration;

        if (
            snapshot is not null
            && duration > TimeSpan.Zero
            && DateTimeOffset.UtcNow - snapshot.LoadedAt < duration
        )
        {
            tokens = snapshot.Tokens;
            return true;
        }

        tokens = null!;
        return false;
    }

    private sealed record Snapshot(
        IReadOnlyDictionary<string, AccessToken> Tokens,
        DateTimeOffset LoadedAt
    );
}
