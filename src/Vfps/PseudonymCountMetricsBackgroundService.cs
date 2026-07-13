using Microsoft.Extensions.Logging;
using Prometheus;
using Vfps.Data;

namespace Vfps;

/// <summary>
/// Periodically refreshes a per-namespace pseudonym count gauge from the database. Deliberately
/// a Gauge set from a real query on a timer, not a counter incremented per pseudonym creation:
/// an in-process counter would reset to 0 on every restart and would only ever reflect the
/// handling replica's own share of requests in a horizontally-scaled deployment, neither of which
/// represents "how many pseudonyms exist in this namespace" - a Gauge sourced from the shared
/// database is correct regardless of restarts or replica count (every replica queries the same
/// underlying data, so use max()/avg(), not sum(), when graphing across replicas).
/// </summary>
public class PseudonymCountMetricsBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<PseudonymCountMetricsBackgroundService> logger
) : BackgroundService
{
    // COUNT(*) GROUP BY namespace_name is a full-scan-class query at the "hundreds of millions
    // of rows" scale this service targets - kept infrequent (rather than matching
    // MemoryCacheMetricsBackgroundService's 60s, which only reads in-memory cache stats) since
    // this one pays a real, bounded database cost each tick.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private static readonly Gauge PseudonymsPerNamespace = Metrics.CreateGauge(
        "vfps_pseudonyms",
        "Current number of pseudonyms per namespace, refreshed periodically from the database. "
            + "A namespace with no pseudonyms yet simply has no series here.",
        "namespace"
    );

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        do
        {
            await RefreshAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var pseudonymRepository =
                scope.ServiceProvider.GetRequiredService<IPseudonymRepository>();
            var counts = await pseudonymRepository.CountAllGroupedByNamespaceAsync(
                cancellationToken
            );

            foreach (var (namespaceName, count) in counts)
            {
                PseudonymsPerNamespace.WithLabels(namespaceName).Set(count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a transient DB issue here shouldn't crash the whole host over a
            // metrics refresh - see the identical reasoning on S3LifecyclePolicyBackgroundService.
            logger.LogError(ex, "Failed to refresh the per-namespace pseudonym count metric.");
        }
    }
}
