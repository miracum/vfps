using Vfps.Metrics;

namespace Vfps;

/// <summary>
/// Keeps this replica's <see cref="PseudonymCountMetrics"/> gauge in step with the shared snapshot.
///
/// Runs everywhere and unconditionally, and deliberately does *not* compute anything: the expensive
/// recompute is a Hangfire recurring job that runs on one replica per interval (see Program.cs), so
/// all this does per tick is a single-row read. That's cheap enough to run far more often than the
/// recompute, which is the point - it bounds how far behind the shared snapshot any individual
/// replica's exported values can be, and it's what lets a freshly started replica export a real
/// value immediately rather than nothing until the next recompute.
///
/// Not folded into the Hangfire job itself precisely because it has to happen on every replica: a
/// job runs on whichever server picks it up, which would leave every other replica exporting stale
/// or absent series.
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
                await metrics.PublishFromSnapshotAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown - WaitForNextTickAsync throws once stoppingToken is cancelled.
        }
    }
}
