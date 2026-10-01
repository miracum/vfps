using System.Diagnostics.Metrics;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.Metrics;

/// <summary>
/// Publishes when each service-account token expires, so the metrics backend can alert before a
/// pipeline's credential stops working. Service-account tokens only: one of those running out is
/// an outage nobody is signed in to notice, whereas a personal token's owner is the one who uses
/// it, and is told on the Access Tokens page instead (see
/// <see cref="Config.AccessTokenConfig.ExpiryWarningPeriod"/>).
///
/// The value is the expiry instant, not the time remaining - the same convention as cert-manager's
/// certificate expiry metric. It only changes when a token does, and the alert rule subtracts
/// <c>time()</c> itself, so how much warning is wanted is decided with the rest of the alerting
/// config rather than here.
///
/// A token stays reported after it has expired, until it is revoked or deleted. Reporting only
/// live tokens would make a series vanish at exactly the moment its alert matters most, resolving
/// the alert rather than escalating it. Revoking the old token once its replacement is rolled out
/// - the rotation a service account exists to make possible - is what clears it.
///
/// Read directly by every replica rather than computed once and shared like
/// <see cref="PseudonymCountMetrics"/>: the token table holds one row per issued credential, a
/// read that costs less than coordinating it would. Every replica reports the same values, so
/// aggregate with <c>max()</c> rather than <c>sum()</c>.
/// </summary>
public class ServiceAccountTokenExpiryMetricsBackgroundService(
    IAccessTokenRepository tokenRepository,
    ILogger<ServiceAccountTokenExpiryMetricsBackgroundService> logger,
    TimeSpan? pollInterval = null
) : BackgroundService
{
    // Only ever overridden by tests - same pattern (and rationale) as
    // PseudonymCountMetricsBackgroundService's own pollInterval parameter.
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(60);

    // Replaced wholesale on every refresh, so a token that has been revoked or deleted simply stops
    // being reported - the same reason PseudonymCountMetrics swaps its dictionary, and the reason
    // this is an observable gauge: a synchronous Gauge<long> would keep exporting a gone token's
    // last recorded value forever.
    private static volatile IReadOnlyList<TokenExpiry> latestExpiries = [];

    // Exported as "vfps_service_account_token_expiration_timestamp_seconds". The name already ends
    // in the unit, so the Prometheus exporter doesn't append it a second time.
    private static readonly ObservableGauge<long> ExpirationTimestamp =
        Program.Meter.CreateObservableGauge(
            "vfps.service_account.token.expiration.timestamp.seconds",
            ObserveExpiries,
            unit: "s",
            description: "When each unrevoked service-account token expires, as a Unix timestamp. "
                + "Expired tokens keep being reported until they are revoked or deleted."
        );

    private static IEnumerable<Measurement<long>> ObserveExpiries() =>
        latestExpiries.Select(expiry => new Measurement<long>(
            expiry.ExpiresAt.ToUnixTimeSeconds(),
            new KeyValuePair<string, object?>("service_account", expiry.ServiceAccountName),
            new KeyValuePair<string, object?>("token_id", expiry.TokenId),
            new KeyValuePair<string, object?>("token_name", expiry.TokenName)
        ));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            do
            {
                await RefreshAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown - WaitForNextTickAsync throws once stoppingToken is cancelled.
        }
    }

    /// <summary>Re-reads the tokens and replaces what the gauge reports.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tokens = await tokenRepository.GetAllUnrevokedAsync(cancellationToken);

            latestExpiries =
            [
                .. tokens
                    .Where(token => token.TokenType == AccessTokenType.ServiceAccount)
                    .Select(token => new TokenExpiry(
                        token.ServiceAccountName ?? string.Empty,
                        token.TokenId,
                        token.Name,
                        token.ExpiresAt
                    )),
            ];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort, and the previous values are deliberately kept rather than cleared - see
            // the identical reasoning on PseudonymCountMetrics.PublishFromSnapshotAsync. Here it
            // matters more: cleared series would silently resolve every firing expiry alert.
            logger.LogError(ex, "Failed to refresh the service account token expiry metric.");
        }
    }

    private sealed record TokenExpiry(
        string ServiceAccountName,
        string TokenId,
        string TokenName,
        DateTimeOffset ExpiresAt
    );
}
