using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Vfps.AppServices;
using Vfps.Config;

namespace Vfps.CsvProcessing;

/// <summary>
/// Applies the CSV job bucket's configuration on startup:
/// - a lifecycle rule that expires (deletes) objects under the "csv-jobs/" prefix after
///   <see cref="S3Config.ObjectRetentionDays"/> days - both the original uploaded CSV and its
///   pseudonymized output. Job records in Postgres are never deleted, so without this, the
///   objects they reference - including the original, unpseudonymized input - would otherwise
///   live in the bucket forever.
/// - a CORS rule allowing <see cref="S3Config.AllowedOrigins"/> to PUT/GET objects directly
///   against presigned URLs (see wwwroot/js/csvUpload.js). The browser talks to the bucket on a
///   different origin than vfps itself is served from, so without this the bucket never sends
///   back an Access-Control-Allow-Origin header, and the browser blocks the (preflighted,
///   because csvUpload.js sets a Content-Type header) upload before it ever reaches S3.
/// </summary>
/// <remarks>
/// PutLifecycleConfiguration and PutCORSConfiguration each replace the bucket's entire respective
/// configuration rather than merging into it, so this assumes a bucket dedicated to vfps (as the
/// bundled compose.yaml sets up) rather than one shared with unrelated rules.
/// </remarks>
public class S3BucketConfigurationBackgroundService(
    IAmazonS3 s3,
    IOptions<S3Config> s3Config,
    ILogger<S3BucketConfigurationBackgroundService> logger
) : BackgroundService
{
    private const string LifecycleRuleId = "vfps-csv-jobs-retention";
    private const string CorsRuleId = "vfps-csv-jobs-cors";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = s3Config.Value;

        // Applied independently - one failing (e.g. a permissions issue scoped to only one of
        // the two actions) shouldn't prevent the other from being attempted.
        await ApplyLifecyclePolicyAsync(config, stoppingToken);
        await ApplyCorsConfigurationAsync(config, stoppingToken);
    }

    private async Task ApplyLifecyclePolicyAsync(S3Config config, CancellationToken stoppingToken)
    {
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
                                Id = LifecycleRuleId,
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

    private async Task ApplyCorsConfigurationAsync(S3Config config, CancellationToken stoppingToken)
    {
        if (config.AllowedOrigins.Count == 0)
        {
            logger.LogInformation(
                "S3:AllowedOrigins is empty - leaving the bucket's CORS configuration untouched."
            );
            return;
        }

        logger.LogInformation(
            "Applying an S3 CORS rule allowing {AllowedOrigins} to PUT/GET objects directly against the bucket.",
            string.Join(", ", config.AllowedOrigins)
        );

        try
        {
            await s3.PutCORSConfigurationAsync(
                new PutCORSConfigurationRequest
                {
                    BucketName = config.Bucket,
                    Configuration = new CORSConfiguration
                    {
                        Rules =
                        [
                            new CORSRule
                            {
                                Id = CorsRuleId,
                                AllowedOrigins = [.. config.AllowedOrigins],
                                AllowedMethods = ["GET", "PUT"],
                                // "*" is the documented S3 wildcard for "allow any request
                                // header" - needed because the presigned PUT's signed headers
                                // (and therefore the browser's preflight Access-Control-Request-
                                // Headers) are Content-Type plus whatever the AWSSDK client adds,
                                // which isn't worth hand-enumerating here.
                                AllowedHeaders = ["*"],
                                MaxAgeSeconds = 3600,
                            },
                        ],
                    },
                },
                stoppingToken
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort, same rationale as the lifecycle rule above.
            logger.LogError(
                ex,
                "Failed to apply the S3 CORS configuration - browser uploads/downloads via "
                    + "presigned URLs will be blocked by CORS until this is resolved."
            );
        }
    }
}
