using System.Diagnostics.Metrics;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.Metrics;

/// <summary>
/// Publishes the per-namespace pseudonym count gauge. Deliberately a gauge read from a real query
/// on a schedule, not a counter incremented per pseudonym creation: an in-process counter would
/// reset to 0 on every restart and would only ever reflect the handling replica's own share of
/// requests in a horizontally-scaled deployment, neither of which represents "how many pseudonyms
/// exist in this namespace".
///
/// The count itself is a full-scan-class GROUP BY over the largest table in the schema, so it's
/// computed by one replica and shared through a <see cref="MetricSnapshot"/> row rather than
/// recomputed everywhere - otherwise this one metric's database cost would grow with the replica
/// count, i.e. scaling out for availability would make the deployment less resilient rather than
/// more. Every replica then exports the same stored values, which is what makes the series
/// consistent across pods (any aggregation agrees, and there are no orphaned series left behind on
/// a replica that computed it once) and what lets a freshly started replica export a real value
/// immediately instead of nothing until its first recompute.
/// </summary>
public class PseudonymCountMetrics(
    IPseudonymRepository pseudonymRepository,
    IMetricSnapshotRepository snapshotRepository,
    ILogger<PseudonymCountMetrics> logger,
    TimeSpan? recomputeInterval = null
)
{
    // How stale the stored snapshot has to be before a replica recomputes it. Infrequent, because
    // this is the expensive half - unlike the read, which every replica does on a far shorter
    // interval (see PseudonymCountMetricsBackgroundService). Only ever overridden by tests - same
    // pattern (and rationale) as StalledPseudonymizationJobWatchdogService's own checkInterval.
    private readonly TimeSpan _recomputeInterval = recomputeInterval ?? TimeSpan.FromMinutes(5);

    // Replaced wholesale on every read rather than mutated key by key, so a namespace that no
    // longer exists (or no longer has any pseudonyms) simply stops being reported instead of being
    // pinned at its last observed value forever.
    private static volatile IReadOnlyDictionary<string, long> latestCounts =
        new Dictionary<string, long>();

    // An *observable* gauge, not a synchronous one: a replica that hasn't managed to read the
    // snapshot yet has nothing to report, and a synchronous Gauge<long> offers no way to say that -
    // its last recorded measurement would keep being exported indefinitely. An observable
    // instrument that yields nothing simply omits the series for that scrape.
    //
    // Dotted name is the OpenTelemetry convention; the Prometheus exporter renders it with
    // underscores on export ("vfps_pseudonyms"), matching the metric name this service exposed
    // under prometheus-net. Every replica reports the same database-wide figure, so graph it with
    // max() or avg() across replicas - summing would multiply it by the replica count.
    private static readonly ObservableGauge<long> PseudonymsPerNamespace =
        Program.Meter.CreateObservableGauge(
            "vfps.pseudonyms",
            ObserveCounts,
            description: "Current number of pseudonyms per namespace, refreshed periodically from "
                + "the database. A namespace with no pseudonyms yet simply has no series here."
        );

    private static IEnumerable<Measurement<long>> ObserveCounts() =>
        latestCounts.Select(entry => new Measurement<long>(
            entry.Value,
            new KeyValuePair<string, object?>("namespace", entry.Key)
        ));

    /// <summary>
    /// Recomputes the snapshot if this replica wins the claim and it's due, then republishes the
    /// gauge from whatever the stored snapshot currently holds. Safe (and expected) to call far
    /// more often than the recompute interval: the claim is what keeps the expensive half rare.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (
                await snapshotRepository.TryClaimRefreshAsync(
                    MetricSnapshot.PseudonymCountsName,
                    _recomputeInterval,
                    cancellationToken
                )
            )
            {
                var counts = await pseudonymRepository.CountAllGroupedByNamespaceAsync(
                    cancellationToken
                );

                await snapshotRepository.WriteAsync(
                    MetricSnapshot.PseudonymCountsName,
                    counts,
                    cancellationToken
                );
            }

            // Read back unconditionally, including on the ticks where another replica did the
            // computing - that read is the only reason every replica can export the same values.
            latestCounts = await snapshotRepository.ReadAsync(
                MetricSnapshot.PseudonymCountsName,
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a transient DB issue here shouldn't take anything else down over a
            // metrics refresh - see the identical reasoning on S3BucketConfigurationBackgroundService.
            // The previous snapshot is deliberately left in place rather than cleared: a failed
            // refresh means "we don't know any more", and a slightly stale count is a better answer
            // to that than a series that vanishes on every transient blip.
            logger.LogError(ex, "Failed to refresh the per-namespace pseudonym count metric.");
        }
    }
}
