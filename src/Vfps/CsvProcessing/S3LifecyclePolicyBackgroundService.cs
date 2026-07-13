using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Vfps.AppServices;
using Vfps.Config;

namespace Vfps.CsvProcessing;

/// <summary>
/// Applies an S3 bucket lifecycle rule on startup that expires (deletes) objects under the CSV
/// job "csv-jobs/" prefix after <see cref="S3Config.ObjectRetentionDays"/> days - both the
/// original uploaded CSV and its pseudonymized output. Job records in Postgres are never
/// deleted, so without this, the objects they reference - including the original,
/// unpseudonymized input - would otherwise live in the bucket forever.
/// </summary>
/// <remarks>
/// PutLifecycleConfiguration replaces the bucket's entire lifecycle configuration rather than
/// merging into it, so this assumes a bucket dedicated to vfps (as the bundled compose.yaml sets
/// up) rather than one shared with unrelated lifecycle rules.
/// </remarks>
public class S3LifecyclePolicyBackgroundService(
    IAmazonS3 s3,
    IOptions<S3Config> s3Config,
    ILogger<S3LifecyclePolicyBackgroundService> logger
) : BackgroundService
{
    private const string RuleId = "vfps-csv-jobs-retention";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = s3Config.Value;
        if (config.ObjectRetentionDays <= 0)
        {
            logger.LogInformation(
                "S3:ObjectRetentionDays is 0 - leaving the bucket's lifecycle configuration untouched."
            );
            return;
        }

        logger.LogInformation(
            "Applying an S3 lifecycle rule to expire objects under {Prefix} after {Days} day(s).",
            PseudonymizationJobAppService.S3ObjectKeyPrefix,
            config.ObjectRetentionDays
        );

        try
        {
            await s3.PutLifecycleConfigurationAsync(
                new PutLifecycleConfigurationRequest
                {
                    BucketName = config.Bucket,
                    Configuration = new LifecycleConfiguration
                    {
                        Rules =
                        [
                            new LifecycleRule
                            {
                                Id = RuleId,
                                Filter = new LifecycleFilter
                                {
                                    LifecycleFilterPredicate = new LifecyclePrefixPredicate
                                    {
                                        Prefix = PseudonymizationJobAppService.S3ObjectKeyPrefix,
                                    },
                                },
                                Status = LifecycleRuleStatus.Enabled,
                                Expiration = new LifecycleRuleExpiration
                                {
                                    Days = config.ObjectRetentionDays,
                                },
                            },
                        ],
                    },
                },
                stoppingToken
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: an unhandled exception here would otherwise crash the entire host (an
            // unhandled BackgroundService exception stops the whole app, not just this feature),
            // taking down namespace/pseudonym access along with it over what's ultimately a
            // bucket housekeeping rule. CSV job upload/processing/download don't depend on this
            // having succeeded, so log and let startup continue - a bucket permissions issue or a
            // transient endpoint error shouldn't be fatal to the whole application.
            logger.LogError(
                ex,
                "Failed to apply the S3 lifecycle rule for {Prefix} - uploaded/processed CSV "
                    + "files will not be automatically deleted until this is resolved.",
                PseudonymizationJobAppService.S3ObjectKeyPrefix
            );
        }
    }
}
