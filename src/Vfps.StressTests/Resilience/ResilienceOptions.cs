using System.Globalization;

namespace Vfps.StressTests.Resilience;

/// <summary>
/// Configuration for one HA resilience run, read from the environment so that
/// <c>tests/chaos/ha/run.sh</c> is the single source of truth for timings - the chaos schedule and
/// the load window have to agree on a clock, and duplicating the durations in two places is the
/// easiest way for them to silently drift apart.
/// </summary>
public sealed record ResilienceOptions
{
    public required Uri GrpcAddress { get; init; }
    public required string NamespaceName { get; init; }

    /// <summary>
    /// Requests issued per second, held constant for the whole run regardless of how the service is
    /// responding. See <see cref="OpenModelLoadRunner"/> for why this is an open model.
    /// </summary>
    public required int RatePerSecond { get; init; }

    public required TimeSpan LoadDuration { get; init; }

    /// <summary>
    /// Quiet period between the end of the load window and the verification pass, so that a
    /// just-promoted primary has finished catching up before P2/P3 are judged. Verification failures
    /// are zero-tolerance, so they must never be able to fire on a cluster that is merely still
    /// settling.
    /// </summary>
    public required TimeSpan SettleDuration { get; init; }

    /// <summary>
    /// Deadline applied to every load-phase call, covering the client's whole retry sequence.
    /// <para>
    /// Without one, P1 cannot tell "served in 8ms" from "served in 22 seconds" - vfps answers
    /// both with <c>OK</c>. A database failover then shows up not as failures but as latency, and
    /// the only thing that eventually registers is <see cref="MaxInFlight"/> saturating, which
    /// turns a graded availability measure into a cliff. A deadline is also simply what a real
    /// caller would set: a pseudonymization call that takes twenty seconds has failed, whatever
    /// status code eventually comes back.
    /// </para>
    /// <para>
    /// Keep <c>MaxInFlight</c> comfortably above <c>RatePerSecond * CallDeadline</c>, or the
    /// ceiling starts shedding before the deadline has a chance to fire and the cliff comes back.
    /// </para>
    /// </summary>
    public required TimeSpan CallDeadline { get; init; }

    /// <summary>
    /// Ceiling on concurrently outstanding calls, as a backstop against unbounded task growth
    /// rather than as a load-management device - <see cref="CallDeadline"/> is what bounds a
    /// stalled call now. Sized well above <c>RatePerSecond * CallDeadline</c> so that shedding
    /// means the harness itself is the bottleneck, which is a test-validity problem and reported
    /// as one.
    /// </summary>
    public required int MaxInFlight { get; init; }

    public required int LedgerCapacity { get; init; }

    /// <summary>
    /// P1, primary gate: the longest run of consecutive seconds in which load was offered and
    /// nothing succeeded.
    /// <para>
    /// Deliberately a duration rather than a failure rate. A rate is a function of run length, so
    /// the same single failover scores 9.5% across a six-minute run and 3.4% across a
    /// seventeen-minute one - identical behaviour, opposite verdicts, decided only by which
    /// scenario set happened to run. "No disruption blacks the service out for longer than N
    /// seconds" holds its meaning across both, and is the number an operator actually cares about.
    /// </para>
    /// </summary>
    public required int MaxOutageSeconds { get; init; }

    /// <summary>
    /// P1, secondary gate: total failure-equivalent seconds (failed plus shed, over the offered
    /// rate) across the whole run.
    /// <para>
    /// Catches what <see cref="MaxOutageSeconds"/> cannot - a service that is chronically flaky
    /// rather than briefly and completely down, where scattered failures never black out a whole
    /// second. Scales with the number of disruptive scenarios, so the weekly run is given a larger
    /// allowance than a trimmed pull-request run.
    /// </para>
    /// </summary>
    public required double MaxUnavailableSeconds { get; init; }

    public required int VerificationConcurrency { get; init; }

    public static ResilienceOptions FromEnvironment() =>
        new()
        {
            GrpcAddress = new Uri(
                Environment.GetEnvironmentVariable("VFPS_GRPC_ADDRESS") ?? "http://localhost:8081"
            ),
            NamespaceName = Env("RESILIENCE_NAMESPACE", "resilience"),
            RatePerSecond = EnvInt("RESILIENCE_RATE_PER_SECOND", 50),
            LoadDuration = TimeSpan.FromSeconds(EnvInt("RESILIENCE_LOAD_SECONDS", 960)),
            SettleDuration = TimeSpan.FromSeconds(EnvInt("RESILIENCE_SETTLE_SECONDS", 60)),
            CallDeadline = TimeSpan.FromSeconds(EnvInt("RESILIENCE_CALL_DEADLINE_SECONDS", 5)),
            MaxInFlight = EnvInt("RESILIENCE_MAX_IN_FLIGHT", 500),
            LedgerCapacity = EnvInt("RESILIENCE_LEDGER_CAPACITY", 2000),
            // Provisional, from two observed CI runs (16s longest outage, 34s total across the
            // trimmed two-disruption set) with roughly 2x headroom. Revisit as runs accumulate -
            // but keep them as durations, not rates.
            //
            // MaxOutage is the same for every scenario set, which is the point of it being a
            // duration. MaxUnavailable is not: it grows with the number of disruptions, so this
            // default is sized for the full weekly set and the workflow tightens it for the
            // trimmed pull-request run.
            MaxOutageSeconds = EnvInt("RESILIENCE_MAX_OUTAGE_SECONDS", 30),
            MaxUnavailableSeconds = EnvDouble("RESILIENCE_MAX_UNAVAILABLE_SECONDS", 150),
            VerificationConcurrency = EnvInt("RESILIENCE_VERIFY_CONCURRENCY", 16),
        };

    private static string Env(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(
            Environment.GetEnvironmentVariable(name),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed
        )
            ? parsed
            : fallback;

    private static double EnvDouble(string name, double fallback) =>
        double.TryParse(
            Environment.GetEnvironmentVariable(name),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed
        )
            ? parsed
            : fallback;
}
