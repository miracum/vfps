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
    /// P1: the fraction of calls allowed to fail after the client's own retry budget is exhausted.
    /// <strong>This default is a placeholder, not a calibrated value.</strong> Run the workflow a few
    /// times with <c>SCENARIOS=baseline</c> and set it from the observed ambient failure rate plus
    /// headroom - a budget picked before seeing a baseline is the usual reason chaos jobs end up
    /// permanently marked continue-on-error.
    /// </summary>
    public required double ErrorBudget { get; init; }

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
            ErrorBudget = EnvDouble("RESILIENCE_ERROR_BUDGET", 0.005),
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
