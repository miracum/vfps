using Vfps.Data;

namespace Vfps.Metrics;

/// <summary>
/// Refreshes the batch gauges in <see cref="BatchMetrics"/> from the database on a timer, rather
/// than querying on every scrape: a scrape callback runs synchronously, and a slow database would
/// then stall the whole metrics endpoint. On a failed read the previous values stay published -
/// the gauges go stale rather than dropping to zero, which an alert would read as "all clear".
/// </summary>
public sealed class BatchMetricsBackgroundService(
    IPseudonymizationJobRepository jobRepository,
    ILogger<BatchMetricsBackgroundService> logger,
    TimeSpan? pollInterval = null
) : BackgroundService
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);

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
            // Normal shutdown.
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        // On every refresh rather than once: a measurement made before the OpenTelemetry listener
        // subscribes is dropped, and this service starts before it does. Adding 0 again is free.
        BatchMetrics.InitializeFinishedSeries();

        try
        {
            BatchMetrics.Publish(await jobRepository.GetMetricsSnapshotAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to refresh the batch metrics.");
        }
    }
}
