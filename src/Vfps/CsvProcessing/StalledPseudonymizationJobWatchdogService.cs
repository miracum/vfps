using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.CsvProcessing;

/// <summary>
/// Catches CSV pseudonymization jobs that got stuck showing <see cref="PseudonymizationJobStatus.Running"/>
/// in the UI forever with no further progress. This happens when
/// <see cref="CsvPseudonymizationJobRunner"/> can't get to record its own outcome - e.g. it
/// crashed, was killed, or a database outage outlasted its own retry budget (see
/// EnableRetryOnFailure in Program.cs) right as it was trying to report progress or mark itself
/// Failed. Hangfire's own "move to Failed" retry on top of that is a fixed, unconfigurable ~45s
/// (10 attempts) in the version this app uses, so a multi-minute outage can leave a job with no
/// party left trying to update it at all - this periodic sweep is the backstop that eventually
/// notices and fixes the display.
/// </summary>
public class StalledPseudonymizationJobWatchdogService(
    IServiceProvider serviceProvider,
    IOptions<CsvProcessingConfig> csvProcessingConfig,
    ILogger<StalledPseudonymizationJobWatchdogService> logger,
    TimeSpan? checkInterval = null
) : BackgroundService
{
    // Only ever overridden by tests, which need a far shorter interval to observe a tick without
    // a real-time wait - production callers always go through the DI-resolved constructor, which
    // never supplies this, so they get the real one-minute cadence.
    private readonly TimeSpan _checkInterval = checkInterval ?? TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_checkInterval);
        try
        {
            // Waits for the first tick rather than checking immediately on startup - a restart
            // (e.g. an app upgrade) is exactly when a job that's actually still fine is most
            // likely to look "stalled" for a moment (its last progress update is however old the
            // restart took), so checking the instant this instance comes up is the worst possible
            // time to judge that. Waiting for the first regular tick gives a job that was already
            // mid-processing across the restart a full check interval to prove itself alive again
            // before this service passes any verdict on it.
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await CheckForStalledJobsAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown - WaitForNextTickAsync throws once stoppingToken is cancelled.
        }
    }

    private async Task CheckForStalledJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var jobRepository =
                scope.ServiceProvider.GetRequiredService<IPseudonymizationJobRepository>();

            var stalledJobIds = await jobRepository.FindStalledRunningJobIdsAsync(
                csvProcessingConfig.Value.StalledJobThreshold,
                cancellationToken
            );

            foreach (var jobId in stalledJobIds)
            {
                logger.LogWarning(
                    "Pseudonymization job {JobId} had no progress update in over {Threshold} - "
                        + "marking it Stalled.",
                    jobId,
                    csvProcessingConfig.Value.StalledJobThreshold
                );

                // Stalled, not Failed - this is a heuristic guess ("looks dead"), not a confirmed
                // failure, and occasionally wrong (e.g. a restart delayed progress updates past
                // the threshold on a job that was still fine). Keeping it distinct from a run that
                // actually hit an exception lets the UI say so instead of implying the processing
                // itself failed.
                await jobRepository.UpdateStatusAsync(
                    jobId,
                    PseudonymizationJobStatus.Stalled,
                    "Processing appears to have stalled (no progress update in over "
                        + $"{csvProcessingConfig.Value.StalledJobThreshold}) - the worker may "
                        + "have crashed, lost its database connection, or been killed by an app "
                        + "restart. See server logs for details.",
                    cancellationToken
                );
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a transient DB issue while checking for stalled jobs shouldn't crash
            // the whole host - see the identical reasoning on PseudonymCountMetricsBackgroundService
            // / S3BucketConfigurationBackgroundService. Just try again next tick.
            logger.LogError(ex, "Failed to check for stalled pseudonymization jobs.");
        }
    }
}
