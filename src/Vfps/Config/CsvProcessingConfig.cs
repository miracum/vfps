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

    /// <summary>
    /// Whether this instance actually runs CSV jobs, as opposed to only accepting them.
    ///
    /// Enqueueing a job, and the Hangfire dashboard, work either way - those need Hangfire's
    /// client and storage, which are registered whenever S3 is enabled. This flag controls only
    /// the processing loop, which is what lets a deployment run dedicated worker pods: set it to
    /// false on the pods serving the API and admin UI, and true on a second Deployment running
    /// the same image. That separation exists because one Deployment can only have one shutdown
    /// window and one resource budget, and a long CSV job wants a long drain while an API pod
    /// wants a short one.
    ///
    /// Left true by default so a single Deployment keeps behaving exactly as before. **If every
    /// instance sets it to false, nothing processes jobs** and they queue forever - the admin UI's
    /// job list shows them stuck in Queued, and the Hangfire dashboard's Servers page is empty.
    /// </summary>
    public bool ProcessJobs { get; set; } = true;

    /// <summary>
    /// How many CSV jobs one replica processes concurrently (Hangfire's worker count for this
    /// app's job server). Pinned rather than left at Hangfire's own default of
    /// <c>min(ProcessorCount * 5, 20)</c>, because that default is chosen for short, cheap jobs
    /// and knows nothing about this app's real constraint: the shared Npgsql connection pool. A
    /// single de-pseudonymizing job resolves a chunk via up to
    /// CsvPseudonymizationJobRunner.DepseudonymizeConcurrencyChunkSize (20) concurrent lookups, so
    /// 20 workers would be up to 400 concurrent connection requests from one replica against a
    /// pool whose default maximum is 100 - jobs would then spend their time queued on the pool
    /// (and eventually time out on the connection string's Timeout) rather than working. 4 keeps
    /// the worst case (~80) inside that default pool while still overlapping jobs, and is the
    /// knob to raise in step with `Maximum Pool Size` if a deployment wants more concurrency.
    ///
    /// Note this multiplies by replica count against a *shared* database: 4 workers on each of 3
    /// replicas is 12 concurrent jobs, not 4.
    /// </summary>
    public int WorkerCount { get; set; } = 4;

    /// <summary>
    /// How long the Hangfire job server waits for its in-flight jobs to wind down when the app is
    /// shutting down (e.g. a rolling upgrade). A CSV job can't checkpoint and resume, so waiting
    /// longer doesn't let one finish - what this window actually buys is time for the runner to
    /// unwind cleanly after CsvPseudonymizationJobRunner's per-row
    /// ThrowIfCancellationRequested() fires, and for a job that happened to be nearly done to
    /// record its own outcome instead of being cut off mid-write.
    ///
    /// Must stay comfortably below the app-level ShutdownTimeout (which in turn must stay below
    /// the pod's terminationGracePeriodSeconds), or the host gives up on this service before it
    /// has finished stopping and SIGKILL arrives mid-unwind anyway.
    /// </summary>
    public TimeSpan JobServerShutdownTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a job orphaned by a replica disappearing (a rolling upgrade, an OOM kill, a node
    /// failure) waits before another replica picks it up and reprocesses it. This is Hangfire's
    /// dead-server detection window; its own default is 5 minutes, which combined with this app's
    /// <see cref="StalledJobThreshold"/> of 10 minutes leaves an uncomfortably narrow margin - the
    /// two mechanisms race, and whenever the stall watchdog wins, the job is flagged Stalled in
    /// the UI even though it was about to be re-dispatched and completed normally (see
    /// StalledPseudonymizationJobWatchdogService for why that verdict is deliberately not
    /// terminal). Shortening this to 2 minutes makes re-dispatch the reliable winner, so a rolling
    /// upgrade costs a job a couple of minutes rather than a scary status and a ~5+ minute wait.
    ///
    /// Hangfire's heartbeat and server-check intervals are derived from this (see Program.cs)
    /// rather than exposed separately, since setting them inconsistently - a heartbeat slower than
    /// this window - would have live servers declare each other dead and double-process jobs.
    /// Values below 30 seconds are clamped for that same reason.
    /// </summary>
    public TimeSpan OrphanedJobRecoveryDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Source values (matched case-insensitively, after trimming) treated as "no value" rather
    /// than pseudonymized/de-pseudonymized - see
    /// CsvPseudonymizationJobRunner.IsMissingValue. A genuinely blank/whitespace-only cell is
    /// always treated this way regardless of this list; these are additional named placeholders
    /// real-world exports commonly use instead of (or alongside) an actually empty cell.
    /// </summary>
    public List<string> MissingValuePlaceholders { get; set; } = ["NA", "NULL"];
}
