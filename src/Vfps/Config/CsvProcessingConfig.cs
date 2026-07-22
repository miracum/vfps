namespace Vfps.Config;

/// <summary>
/// Tuning knobs for <see cref="CsvProcessing.CsvPseudonymizationJobRunner"/>.
/// </summary>
public class CsvProcessingConfig
{
    /// <summary>
    /// How many rows' worth of values go into one batched upsert round trip when
    /// pseudonymizing - see CsvPseudonymizationJobRunner.FlushChunkPseudonymizeAsync and
    /// IPseudonymRepository.CreateIfNotExistBatchAsync. Doesn't apply to de-pseudonymization,
    /// which resolves a chunk via concurrent single-value lookups bounded by the connection pool
    /// instead (DepseudonymizeConcurrencyChunkSize, a constant - that one protects the shared
    /// connection pool from a single job monopolizing it, so it isn't something an operator
    /// should tune up without also considering every other consumer of that same pool).
    ///
    /// Benchmarked locally against Postgres, fresh (non-conflicting) rows, 5 reps per size:
    /// throughput is ~30-35us/row from 100 rows upward with no cliff through 5000 (100 rows:
    /// ~5ms/batch; 1000: ~26-54ms; 2000: ~58-71ms; 5000: ~153-168ms) - below ~100 rows per-batch
    /// fixed overhead dominates instead (20 rows: ~2-4ms warm, i.e. ~150-200us/row, worse per-row
    /// than larger batches despite the smaller batch). 1000 was chosen over pushing higher because
    /// it already cuts round trips ~50x versus the pre-batching default of 20 rows per chunk while
    /// keeping typical single-batch latency around 30ms - a single in-flight batch can't be
    /// interrupted mid-flight (it isn't wired to a cancellation token) or reflected in the
    /// progress bar until it completes, so this bounds that worst case to something imperceptible
    /// rather than trading it away for the comparatively small further reduction in round trips a
    /// much larger batch (e.g. 5000) would buy.
    /// </summary>
    public int PseudonymizeBatchSize { get; set; } = 1000;

    /// <summary>
    /// How long a job can sit in <see cref="Data.Models.PseudonymizationJobStatus.Running"/>
    /// with no progress update before <see cref="CsvProcessing.StalledPseudonymizationJobWatchdogService"/>
    /// marks it Failed. A healthy job updates its progress at least every ~2s/200 rows (see
    /// CsvPseudonymizationJobRunner), so this exists to catch a runner that crashed, was killed,
    /// or hit a database outage long enough to exhaust its own retries (see
    /// EnableRetryOnFailure in Program.cs) without ever getting to record its own failure -
    /// otherwise that job would show as "Running" in the UI forever. Kept comfortably above the
    /// retry budget there (worst case a few minutes) to avoid flagging a job that's still
    /// legitimately retrying through a transient outage.
    /// </summary>
    public TimeSpan StalledJobThreshold { get; set; } = TimeSpan.FromMinutes(10);
}
