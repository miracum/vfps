using System.Collections.Concurrent;
using Vfps.StressTests.Resilience;

namespace Vfps.StressTests;

/// <summary>
/// The HA chaos test's in-cluster half. Driven by <c>tests/chaos/ha/run.sh</c>, which applies the
/// chaos schedule from outside the cluster while this runs as a Job in its own namespace.
/// </summary>
/// <remarks>
/// Gated behind a trait so it never runs alongside the ordinary stress simulation - it is a
/// quarter-hour scenario that is meaningless without something actively breaking the cluster around
/// it. See <c>docs/testing/ha-chaos-testing.md</c>.
/// </remarks>
[Trait("Category", "Resilience")]
public class ResilienceTests
{
    /// <summary>
    /// Retries the client exhausts before a call is counted as failed.
    /// <para>
    /// Deliberately retries <see cref="StatusCode.Unavailable"/> and nothing else. In particular
    /// <c>Internal</c> is left out: it is how a genuine server-side fault surfaces, so retrying it
    /// would hide precisely what this test exists to measure.
    /// </para>
    /// <para>
    /// <see cref="StatusCode.DeadlineExceeded"/> is deliberately <em>not</em> retried, even though
    /// it would be safe to - <c>Create</c> is idempotent, so a duplicate attempt returns the same
    /// pseudonym rather than minting a second one. A gRPC deadline covers the whole retry sequence,
    /// so retrying after it expires is impossible anyway, and a call that blew its deadline is a
    /// caller-visible failure that P1 should count rather than paper over.
    /// </para>
    /// </summary>
    private static RetryPolicy LoadRetryPolicy =>
        new()
        {
            MaxAttempts = 5,
            InitialBackoff = TimeSpan.FromMilliseconds(200),
            MaxBackoff = TimeSpan.FromSeconds(5),
            BackoffMultiplier = 2,
            RetryableStatusCodes = { StatusCode.Unavailable },
        };

    /// <summary>
    /// Verification runs after chaos has stopped and the cluster has settled, so it can afford to be
    /// patient. It has to be: P2 and P3 are zero-tolerance assertions, and a false positive caused
    /// by a still-reconnecting channel would be worse than no test at all.
    /// </summary>
    private static RetryPolicy VerifyRetryPolicy =>
        new()
        {
            MaxAttempts = 10,
            InitialBackoff = TimeSpan.FromSeconds(1),
            MaxBackoff = TimeSpan.FromSeconds(10),
            BackoffMultiplier = 2,
            RetryableStatusCodes = { StatusCode.Unavailable },
        };

    /// <summary>
    /// Deadline for one verification call, covering its whole retry sequence - same reasoning as
    /// <see cref="ResilienceOptions.CallDeadline"/> on the load phase. Without this, a call whose
    /// connection hangs rather than erroring (a stale subchannel to a backend chaos already killed,
    /// say) never produces a status for <see cref="VerifyRetryPolicy"/> to react to, and blocks
    /// <c>Parallel.ForEachAsync</c> - and with it the timeline this test only emits afterwards -
    /// forever. Sized well above the retry policy's own worst case (10 attempts, backoff capped at
    /// 10s: 1+2+4+8+10x5 = 65s) so it never fires before genuine retries have had their chance.
    /// </summary>
    private static readonly TimeSpan VerifyCallDeadline = TimeSpan.FromSeconds(90);

    // Explicit so a plain `dotnet test` - at the solution level or on this project - skips it. It
    // needs a live kind cluster with Chaos Mesh and CloudNativePG standing behind it, and takes the
    // better part of twenty minutes; without this it would be run by accident far more often than on
    // purpose. tests/chaos/ha/run.sh opts in with `-explicit only`.
    [Fact(Explicit = true)]
    public async Task VfpsKeepsEveryPseudonymItPromisedThroughChaos()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = ResilienceOptions.FromEnvironment();
        var ledger = new PseudonymLedger(options.LedgerCapacity);

        Log($"target          {options.GrpcAddress}");
        Log($"namespace       {options.NamespaceName}");
        Log($"offered rate    {options.RatePerSecond}/s for {options.LoadDuration}");
        Log($"settle          {options.SettleDuration}");
        Log($"max outage      {options.MaxOutageSeconds}s");
        Log($"max unavailable {options.MaxUnavailableSeconds:F0}s");
        Log($"call deadline   {options.CallDeadline.TotalSeconds:F0}s");

        using var loadChannel = CreateChannel(options.GrpcAddress, LoadRetryPolicy);
        using var verifyChannel = CreateChannel(options.GrpcAddress, VerifyRetryPolicy);

        var loadClient = new PseudonymService.PseudonymServiceClient(loadChannel);
        var verifyClient = new PseudonymService.PseudonymServiceClient(verifyChannel);

        await EnsureNamespaceAsync(verifyChannel, options, cancellationToken);

        Log("load window starting");
        var report = await OpenModelLoadRunner.RunAsync(
            options,
            async callCancellationToken =>
            {
                var originalValue = Guid.NewGuid().ToString();

                try
                {
                    var response = await loadClient.CreateAsync(
                        new PseudonymServiceCreateRequest
                        {
                            Namespace = options.NamespaceName,
                            OriginalValue = originalValue,
                        },
                        deadline: DateTime.UtcNow.Add(options.CallDeadline),
                        cancellationToken: callCancellationToken
                    );

                    // Recorded only on success. The ledger is the set of promises the service
                    // actually made to a caller - a failed call promised nothing, and holding it
                    // against a later failover would be wrong.
                    ledger.Offer(originalValue, response.Pseudonym.PseudonymValue);
                    return new CallResult(true, null);
                }
                catch (RpcException exception)
                {
                    return new CallResult(false, exception.StatusCode);
                }
            },
            Log,
            cancellationToken
        );

        var unavailableSeconds = (report.Failed + report.Shed) / (double)options.RatePerSecond;

        Log(
            $"load window complete: {report.Total} offered, {report.Ok} ok, {report.Failed} failed, "
                + $"{report.Shed} shed ({report.FailureRate:P3})"
        );
        Log(
            $"longest outage: {report.LongestOutageSeconds}s (limit {options.MaxOutageSeconds}s) | "
                + $"total unavailable: {unavailableSeconds:F1}s (limit {options.MaxUnavailableSeconds:F0}s)"
        );

        foreach (
            var (status, count) in report.FailuresByStatus.OrderByDescending(entry => entry.Value)
        )
        {
            Log($"  failures {status, -20} {count}");
        }

        // Shedding is meant to be a backstop. When it dominates, the harness ran out of in-flight
        // slots before the deadline could fire, which says more about how this run was configured
        // than about how vfps behaved - and the budget stops being a measure of the service.
        if (report.Shed > report.Failed)
        {
            Log(
                $"NOTE: shed ({report.Shed}) exceeds failed ({report.Failed}). The in-flight ceiling "
                    + $"({options.MaxInFlight}) was reached before the {options.CallDeadline.TotalSeconds:F0}s "
                    + "call deadline could bound the backlog. Raise RESILIENCE_MAX_IN_FLIGHT above "
                    + $"RatePerSecond x CallDeadline (>= {options.RatePerSecond * options.CallDeadline.TotalSeconds:F0}) "
                    + "before reading the failure rate as a property of the service."
            );
        }

        Log($"settling for {options.SettleDuration} before verification");
        await Task.Delay(options.SettleDuration, cancellationToken);

        var pairs = ledger.Snapshot();
        Log($"verifying {pairs.Count} sampled pseudonyms out of {ledger.TotalSeen} created");

        var (stabilityViolations, lookupViolations, verificationErrors) = await VerifyAsync(
            verifyClient,
            options,
            pairs,
            cancellationToken
        );

        EmitTimeline(report);
        ReportViolations("P2 pseudonym stability", stabilityViolations);
        ReportViolations("P3 reverse lookup", lookupViolations);
        ReportViolations("verification errors", verificationErrors);

        // Guards against a vacuous pass. A run in which everything was shed, or the namespace was
        // never usable, would otherwise sail through P2 and P3 with an empty ledger and report
        // success for a cluster that served nothing at all.
        report
            .Ok.Should()
            .BeGreaterThan(0, "the run has to have served real traffic to mean anything");
        pairs.Should().NotBeEmpty("verification over an empty ledger proves nothing");

        // P2 and P3 first: an availability breach is a bad day, but a pseudonym that changed
        // identity is silent data corruption, and it is the more important thing to see in the log.
        stabilityViolations
            .Should()
            .BeEmpty(
                "re-creating an original value must return the pseudonym the service already handed "
                    + "out - a different one means the original write was lost and the subject has "
                    + "silently been split in two"
            );

        lookupViolations
            .Should()
            .BeEmpty("a pseudonym the service issued must still resolve to its original value");

        verificationErrors
            .Should()
            .BeEmpty(
                "verification runs after chaos has stopped, so it should not be failing calls"
            );

        // Primary: how long was it flat out? A single failover of a single-primary PostgreSQL costs
        // an outage by construction - the gate is on that outage staying bounded, not on it being
        // absent.
        report
            .LongestOutageSeconds.Should()
            .BeLessThanOrEqualTo(
                options.MaxOutageSeconds,
                "a disruption should not black the service out for longer than this "
                    + "({0} offered, {1} ok, {2} failed, {3} shed)",
                report.Total,
                report.Ok,
                report.Failed,
                report.Shed
            );

        // Secondary: chronic flakiness that never blacks out a whole second, and so never shows up
        // in the gate above.
        unavailableSeconds
            .Should()
            .BeLessThanOrEqualTo(
                options.MaxUnavailableSeconds,
                "total time the service was in effect not serving, across every scenario"
            );
    }

    private static async Task<(
        ConcurrentBag<string> Stability,
        ConcurrentBag<string> Lookup,
        ConcurrentBag<string> Errors
    )> VerifyAsync(
        PseudonymService.PseudonymServiceClient client,
        ResilienceOptions options,
        IReadOnlyList<PseudonymPair> pairs,
        CancellationToken cancellationToken
    )
    {
        var stability = new ConcurrentBag<string>();
        var lookup = new ConcurrentBag<string>();
        var errors = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(
            pairs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.VerificationConcurrency,
                CancellationToken = cancellationToken,
            },
            async (pair, itemCancellationToken) =>
            {
                // P2. Create is idempotent, so this must return the existing pseudonym rather than
                // generate a new one. A new one means the row backing it is gone.
                try
                {
                    var recreated = await client.CreateAsync(
                        new PseudonymServiceCreateRequest
                        {
                            Namespace = options.NamespaceName,
                            OriginalValue = pair.OriginalValue,
                        },
                        deadline: DateTime.UtcNow.Add(VerifyCallDeadline),
                        cancellationToken: itemCancellationToken
                    );

                    if (
                        !string.Equals(
                            recreated.Pseudonym.PseudonymValue,
                            pair.PseudonymValue,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        stability.Add(
                            $"{pair.OriginalValue}: issued '{pair.PseudonymValue}', "
                                + $"now re-creates as '{recreated.Pseudonym.PseudonymValue}'"
                        );
                    }
                }
                catch (RpcException exception)
                {
                    errors.Add($"Create({pair.OriginalValue}) failed: {exception.StatusCode}");
                }

                // P3. The reverse lookup has to survive too - a partially replicated row or a
                // diverged index would show up here and nowhere else.
                try
                {
                    var fetched = await client.GetAsync(
                        new PseudonymServiceGetRequest
                        {
                            Namespace = options.NamespaceName,
                            PseudonymValue = pair.PseudonymValue,
                        },
                        deadline: DateTime.UtcNow.Add(VerifyCallDeadline),
                        cancellationToken: itemCancellationToken
                    );

                    if (
                        !string.Equals(
                            fetched.Pseudonym.OriginalValue,
                            pair.OriginalValue,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        lookup.Add(
                            $"{pair.PseudonymValue}: resolves to '{fetched.Pseudonym.OriginalValue}', "
                                + $"expected '{pair.OriginalValue}'"
                        );
                    }
                }
                catch (RpcException exception) when (exception.StatusCode == StatusCode.NotFound)
                {
                    lookup.Add($"{pair.PseudonymValue}: NotFound - the pseudonym no longer exists");
                }
                catch (RpcException exception)
                {
                    errors.Add($"Get({pair.PseudonymValue}) failed: {exception.StatusCode}");
                }
            }
        );

        return (stability, lookup, errors);
    }

    private static async Task EnsureNamespaceAsync(
        GrpcChannel channel,
        ResilienceOptions options,
        CancellationToken cancellationToken
    )
    {
        var namespaces = new NamespaceService.NamespaceServiceClient(channel);

        try
        {
            await namespaces.CreateAsync(
                new NamespaceServiceCreateRequest
                {
                    Name = options.NamespaceName,
                    PseudonymGenerationMethod =
                        PseudonymGenerationMethod.SecureRandomBase64UrlEncoded,
                    PseudonymLength = 32,
                    PseudonymPrefix = "res-",
                },
                cancellationToken: cancellationToken
            );
            Log($"created namespace {options.NamespaceName}");
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.AlreadyExists)
        {
            // Re-runs against a surviving cluster are fine: original values are fresh GUIDs every
            // time, so an existing namespace carries no state this run can collide with.
            Log($"namespace {options.NamespaceName} already exists, continuing");
        }
    }

    private static GrpcChannel CreateChannel(Uri address, RetryPolicy retryPolicy) =>
        GrpcChannel.ForAddress(
            address,
            new GrpcChannelOptions
            {
                Credentials = ChannelCredentials.Insecure,
                UnsafeUseInsecureChannelCallCredentials = true,
                MaxRetryAttempts = retryPolicy.MaxAttempts,
                ServiceConfig = new ServiceConfig
                {
                    MethodConfigs =
                    {
                        new MethodConfig
                        {
                            Names = { MethodName.Default },
                            RetryPolicy = retryPolicy,
                        },
                    },
                },
            }
        );

    /// <summary>
    /// Written to stdout between markers so <c>run.sh</c> can lift it straight out of the Job's pod
    /// logs into a CI artifact - no shared volume, and nothing to clean up afterwards.
    /// </summary>
    private static void EmitTimeline(LoadReport report)
    {
        Console.WriteLine("----BEGIN TIMELINE CSV----");
        Console.Write(report.TimelineCsv);
        Console.WriteLine("----END TIMELINE CSV----");
    }

    private static void ReportViolations(string label, ConcurrentBag<string> violations)
    {
        if (violations.IsEmpty)
        {
            Log($"{label}: clean");
            return;
        }

        Log($"{label}: {violations.Count} violation(s)");
        foreach (var violation in violations.Take(50))
        {
            Log($"  {violation}");
        }
    }

    private static void Log(string message) => Console.WriteLine($"[resilience] {message}");
}
