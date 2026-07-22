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
    ILogger<StalledPseudonymizationJobWatchdogService> logger
) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            do
            {
                await CheckForStalledJobsAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
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
                        + "marking it Failed.",
                    jobId,
                    csvProcessingConfig.Value.StalledJobThreshold
                );

                await jobRepository.UpdateStatusAsync(
                    jobId,
                    PseudonymizationJobStatus.Failed,
                    "Processing appears to have stalled (no progress update in over "
                        + $"{csvProcessingConfig.Value.StalledJobThreshold}) - the worker may "
                        + "have crashed or lost its database connection. See server logs for "
                        + "details.",
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
