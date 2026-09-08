using Vfps.Metrics;

namespace Vfps;

/// <summary>
/// Drives <see cref="PseudonymCountMetrics"/> on every replica.
///
/// Runs everywhere and unconditionally, unlike the work it drives: the expensive recompute is
/// rationed by the snapshot's own refresh claim (see
/// <see cref="Data.IMetricSnapshotRepository.TryClaimRefreshAsync"/>), so what this actually does
/// on almost every tick is one conditional UPDATE that matches nothing plus one single-row read.
/// That's cheap enough to run far more often than the recompute interval, which is the point - it
/// bounds how far behind the shared snapshot any individual replica's exported values can be.
/// </summary>
public class PseudonymCountMetricsBackgroundService(
    IServiceProvider serviceProvider,
    TimeSpan? pollInterval = null
) : BackgroundService
{
    // Only ever overridden by tests, which need a far shorter interval to observe a tick without a
    // real-time wait - same pattern (and rationale) as StalledPseudonymizationJobWatchdogService's
    // own checkInterval parameter.
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            do
            {
                using var scope = serviceProvider.CreateScope();
                var metrics = scope.ServiceProvider.GetRequiredService<PseudonymCountMetrics>();
                await metrics.RefreshAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown - WaitForNextTickAsync throws once stoppingToken is cancelled.
        }
    }
}
