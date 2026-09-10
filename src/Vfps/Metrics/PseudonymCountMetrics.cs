using System.Diagnostics.Metrics;
using Hangfire;
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
/// computed by one replica and shared through <see cref="PseudonymCount"/> rows rather than
/// recomputed everywhere - otherwise this one metric's database cost would grow with the replica
/// count, i.e. scaling out for availability would make the deployment less resilient rather than
/// more. Every replica then exports the same stored values, which is what makes the series
/// consistent across pods (any aggregation agrees, and there are no orphaned series left behind on
/// a replica that computed it once) and what lets a freshly started replica export a real value
/// immediately instead of nothing until its first recompute.
///
/// The two halves are driven separately, and that split is the whole design:
/// <list type="bullet">
/// <item><see cref="RecomputeAsync"/> is a Hangfire recurring job (registered in Program.cs), so
/// "exactly one replica per interval" is the scheduler's guarantee rather than something this class
/// coordinates itself.</item>
/// <item><see cref="PublishFromSnapshotAsync"/> runs on every replica on a short timer (see
/// <see cref="PseudonymCountMetricsBackgroundService"/>) and only reads the stored rows.</item>
/// </list>
/// </summary>
public class PseudonymCountMetrics(
    IPseudonymRepository pseudonymRepository,
    IPseudonymCountRepository countRepository,
    ILogger<PseudonymCountMetrics> logger
)
{
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
                + "the database. Every existing namespace is reported, including empty ones, which "
                + "report zero; a series disappears only when its namespace does."
        );

    private static IEnumerable<Measurement<long>> ObserveCounts() =>
        latestCounts.Select(entry => new Measurement<long>(
            entry.Value,
            new KeyValuePair<string, object?>("namespace", entry.Key)
        ));

    /// <summary>
    /// Runs the expensive count and stores the result. Invoked by the Hangfire recurring job, which
    /// is what limits this to one replica per interval.
    /// </summary>
    /// <remarks>
    /// Deliberately lets exceptions escape, unlike <see cref="PublishFromSnapshotAsync"/>: as a
    /// Hangfire job, a throw is how the failure gets recorded and surfaced on the dashboard rather
    /// than only in the logs. The global AutomaticRetryAttribute is set to 0 attempts (see
    /// Program.cs), so a failed run isn't retried - the next scheduled tick is the retry, and a
    /// recompute that's a few minutes late is not worth a retry storm against the largest table in
    /// the schema. The previously stored values stay untouched and keep being exported meanwhile.
    ///
    /// DisableConcurrentExecution because the write replaces the whole set: a second run overlapping
    /// a first (only reachable if one hangs long enough to still be going at the next tick) could
    /// otherwise interleave inserts and deletes. The timeout is generous relative to the ~300ms this
    /// normally takes, and short enough that a waiter fails visibly instead of piling up.
    /// </remarks>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task RecomputeAsync(CancellationToken cancellationToken)
    {
        var counts = await pseudonymRepository.CountAllGroupedByNamespaceAsync(cancellationToken);

        await countRepository.ReplaceAllAsync(counts, DateTimeOffset.UtcNow, cancellationToken);

        logger.LogDebug(
            "Recomputed the per-namespace pseudonym count metric for {NamespaceCount} namespaces.",
            counts.Count
        );
    }

    /// <summary>
    /// Republishes the gauge from whatever the stored snapshot currently holds. Runs on every
    /// replica, including the one that just recomputed - a single read of a single row.
    /// </summary>
    public async Task PublishFromSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            latestCounts = await countRepository.GetAllAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a transient DB issue here shouldn't take anything else down over a
            // metrics read - see the identical reasoning on S3BucketConfigurationBackgroundService.
            // The previous snapshot is deliberately left in place rather than cleared: a failed
            // read means "we don't know any more", and a slightly stale count is a better answer to
            // that than a series that vanishes on every transient blip.
            logger.LogError(ex, "Failed to publish the per-namespace pseudonym count metric.");
        }
    }
}
