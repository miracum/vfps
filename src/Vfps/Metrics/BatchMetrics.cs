using System.Diagnostics.Metrics;
using Vfps.Data.Models;

namespace Vfps.Metrics;

/// <summary>
/// The instruments describing batches (CSV jobs), for alerting on the ones that stop moving or
/// keep failing. Batch state otherwise only lives in the database and the admin UI.
/// </summary>
public static class BatchMetrics
{
    private static volatile BatchMetricsSnapshot latest = BatchMetricsSnapshot.Empty;

    // Exported as "vfps_batches_finished_total". Counted where the transition is written, and only
    // when it actually matched, so a cancel racing a completion is counted once, as what won.
    // Per process - use increase()/rate() across replicas, never the raw value. A batch marked
    // stalled that later completes after all is counted under both outcomes.
    private static readonly Counter<long> Finished = Program.Meter.CreateCounter<long>(
        "vfps.batches.finished",
        unit: "{batch}",
        description: "Batches that reached a final state, by outcome (completed, failed, "
            + "cancelled, stalled). A failed one also carries a reason: unknown_value when the "
            + "batch was set to fail on a value its namespace doesn't know, error otherwise."
    );

    // Exported as "vfps_batches". The same database read by every replica, so every replica
    // reports the same values - aggregate with max(), not sum().
    private static readonly ObservableGauge<long> ByState = Program.Meter.CreateObservableGauge(
        "vfps.batches",
        ObserveByState,
        unit: "{batch}",
        description: "Batches currently stored, by state. Finished ones count until they are "
            + "cleared or deleted."
    );

    // Exported as "vfps_batches_oldest_queued_age_seconds". 0 while nothing is queued.
    private static readonly ObservableGauge<double> OldestQueuedAge =
        Program.Meter.CreateObservableGauge(
            "vfps.batches.oldest_queued.age",
            ObserveOldestQueuedAge,
            unit: "s",
            description: "How long the batch that has been queued the longest has been waiting "
                + "for a worker."
        );

    /// <summary>
    /// Makes every outcome's series exist, at 0 until something happens. Prometheus' increase() and rate() need a sample before
    /// the first increment to see it at all - a series that first appears at 1 reads as no change -
    /// so without this an alert on "any failure in the last hour" misses each process's first one.
    /// </summary>
    public static void InitializeFinishedSeries()
    {
        foreach (var outcome in new[] { "completed", "cancelled", "stalled" })
        {
            Finished.Add(0, new KeyValuePair<string, object?>("outcome", outcome));
        }

        foreach (var reason in Enum.GetValues<FailureReason>())
        {
            Finished.Add(
                0,
                new KeyValuePair<string, object?>("outcome", "failed"),
                new KeyValuePair<string, object?>("reason", ReasonLabel(reason))
            );
        }
    }

    private static string ReasonLabel(FailureReason reason) =>
        reason == FailureReason.UnknownValue ? "unknown_value" : "error";

    /// <summary>Why a batch failed - the <c>reason</c> label on a failed outcome.</summary>
    public enum FailureReason
    {
        /// <summary>
        /// A pseudonymizing batch set to fail on a value its namespace doesn't know met one: the
        /// outcome it was asked to produce for such a file, so nothing for operators to act on.
        /// </summary>
        UnknownValue,

        /// <summary>Anything else - a fault worth looking into.</summary>
        Error,
    }

    /// <summary>Counts a failed batch, labelled with why it failed.</summary>
    public static void RecordFailed(FailureReason reason) =>
        Finished.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "failed"),
            new KeyValuePair<string, object?>("reason", ReasonLabel(reason))
        );

    /// <summary>
    /// Counts a batch reaching <paramref name="status"/>: completed, cancelled or stalled. A
    /// failure goes through <see cref="RecordFailed"/> instead, which knows why.
    /// </summary>
    public static void RecordFinished(PseudonymizationJobStatus status)
    {
        if (
            status
            is PseudonymizationJobStatus.Completed
                or PseudonymizationJobStatus.Cancelled
                or PseudonymizationJobStatus.Stalled
        )
        {
            Finished.Add(1, new KeyValuePair<string, object?>("outcome", OutcomeLabel(status)));
        }
    }

    /// <summary>Replaces what the two gauges report - see <see cref="BatchMetricsBackgroundService"/>.</summary>
    public static void Publish(BatchMetricsSnapshot snapshot) => latest = snapshot;

    private static IEnumerable<Measurement<long>> ObserveByState()
    {
        var snapshot = latest;
        if (!snapshot.IsAvailable)
        {
            yield break;
        }

        // Every state, zeros included: an alert comparing "queued" against "running" needs both
        // series to exist even when one of them is 0.
        foreach (var status in Enum.GetValues<PseudonymizationJobStatus>())
        {
            yield return new Measurement<long>(
                snapshot.CountsByStatus.GetValueOrDefault(status),
                new KeyValuePair<string, object?>("state", OutcomeLabel(status))
            );
        }
    }

    private static IEnumerable<Measurement<double>> ObserveOldestQueuedAge()
    {
        var snapshot = latest;
        if (!snapshot.IsAvailable)
        {
            yield break;
        }

        yield return new Measurement<double>(
            snapshot.OldestQueuedSince is { } since
                ? Math.Max(0, (DateTimeOffset.UtcNow - since).TotalSeconds)
                : 0
        );
    }

    internal static string OutcomeLabel(PseudonymizationJobStatus status) =>
        status switch
        {
            PseudonymizationJobStatus.AwaitingUpload => "awaiting_upload",
            _ => status.ToString().ToLowerInvariant(),
        };
}

/// <summary>One read of the batch table, published to the gauges in <see cref="BatchMetrics"/>.</summary>
public sealed record BatchMetricsSnapshot(
    IReadOnlyDictionary<PseudonymizationJobStatus, long> CountsByStatus,
    DateTimeOffset? OldestQueuedSince,
    bool IsAvailable = true
)
{
    /// <summary>Before the first successful read: the gauges report nothing rather than zeros.</summary>
    public static BatchMetricsSnapshot Empty { get; } =
        new(new Dictionary<PseudonymizationJobStatus, long>(), null, IsAvailable: false);
}
