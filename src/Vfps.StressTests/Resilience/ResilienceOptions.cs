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
    /// Ceiling on concurrently outstanding calls. During a network partition every in-flight call
    /// can block for the connection string's full <c>Timeout=60</c>, so without a ceiling one
    /// 20-second partition at 50/s would leave a thousand threads' worth of tasks outstanding.
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
            MaxInFlight = EnvInt("RESILIENCE_MAX_IN_FLIGHT", 200),
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
