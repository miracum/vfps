using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.Data;

namespace Vfps.Authorization;

/// <summary>
/// Writes back the <see cref="Data.Models.AccessToken.LastUsedAt"/> timestamps
/// <see cref="IAccessTokenUsageTracker"/> has accumulated, on a timer rather than per request.
///
/// Runs on every replica, and only ever does anything on one that has actually authenticated a
/// token since the last tick - an idle replica's flush is a dictionary check and nothing else.
/// Failures are logged and swallowed: this timestamp is informational, and losing an interval's
/// worth of it must never take the service down or interrupt the next flush.
/// </summary>
public class AccessTokenUsageFlushBackgroundService(
    IAccessTokenUsageTracker usageTracker,
    IAccessTokenRepository tokenRepository,
    IOptions<AuthorizationConfig> options,
    ILogger<AccessTokenUsageFlushBackgroundService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.AccessTokens.UsageFlushInterval;
        if (interval <= TimeSpan.Zero)
        {
            interval = TimeSpan.FromMinutes(1);
        }

        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await FlushAsync(stoppingToken);
        }

        // One last flush on the way out, so a replica being rolled doesn't drop the usage it saw
        // in its final interval. Its own token, not the (already cancelled) stoppingToken.
        using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await FlushAsync(shutdownTimeout.Token);
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        var pending = usageTracker.DrainPending();
        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            await tokenRepository.UpdateLastUsedAsync(pending, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to write back the last-used timestamps of {TokenCount} access token(s).",
                pending.Count
            );
        }
    }
}
