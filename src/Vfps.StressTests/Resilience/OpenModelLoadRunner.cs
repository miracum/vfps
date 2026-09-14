using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Vfps.StressTests.Resilience;

/// <summary>Outcome of a single call, after the gRPC client's own retry budget is exhausted.</summary>
public readonly record struct CallResult(bool Succeeded, StatusCode? Status);

/// <summary>
/// Drives a constant offered rate of calls for a fixed window and records what happened, second by
/// second.
/// </summary>
/// <remarks>
/// <para>
/// This is an <strong>open model</strong>: the dispatch loop issues requests on a wall-clock
/// schedule and never waits for a response. That distinction is the difference between a resilience
/// test that measures something and one that does not. A closed model - N virtual users each
/// waiting for its own reply - self-throttles exactly when the service is in trouble, so a database
/// failover shows up as a dip in throughput rather than as errors, systematically understating the
/// impact of the very event under test.
/// </para>
/// <para>
/// Load shedding is therefore counted as a failure rather than quietly dropped: once
/// <see cref="ResilienceOptions.MaxInFlight"/> calls are outstanding, refusing to dispatch is the
/// same self-throttling behaviour by another name, and pretending those calls never happened would
/// reintroduce the closed-model blind spot through the back door.
/// </para>
/// </remarks>
public static class OpenModelLoadRunner
{
    /// <summary>Dispatch batches per second. Ten keeps timer jitter well under a bucket's width.</summary>
    private const int BatchesPerSecond = 10;

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(90);

    public static async Task<LoadReport> RunAsync(
        ResilienceOptions options,
        Func<CancellationToken, Task<CallResult>> issueCall,
        Action<string> log,
        CancellationToken cancellationToken
    )
    {
        var totalSeconds = (int)Math.Ceiling(options.LoadDuration.TotalSeconds) + 1;
        var buckets = new SecondBucket[totalSeconds];
        for (var i = 0; i < buckets.Length; i++)
        {
            buckets[i] = new SecondBucket();
        }

        var failuresByStatus = new ConcurrentDictionary<StatusCode, int>();
        using var inFlight = new SemaphoreSlim(options.MaxInFlight, options.MaxInFlight);
        var clock = Stopwatch.StartNew();

        using (var tick = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / BatchesPerSecond)))
        {
            var carry = 0.0;

            while (
                clock.Elapsed < options.LoadDuration
                && await tick.WaitForNextTickAsync(cancellationToken)
            )
            {
                // Fractional rates (or rates that don't divide evenly into batches) accumulate
                // rather than rounding away, so 5/s really is 5/s and not 0/s.
                carry += options.RatePerSecond / (double)BatchesPerSecond;
                var dispatchCount = (int)carry;
                carry -= dispatchCount;

                // Attributed to the second the call was *offered*, not the second it completed:
                // the timeline exists to be correlated against the chaos schedule, and "requests
                // offered during the partition failed" is the statement worth being able to make.
                // A call that hangs for the connection string's full 60s timeout would otherwise
                // be charged to a window in which nothing was wrong.
                var bucket = BucketFor(buckets, clock.Elapsed);

                for (var i = 0; i < dispatchCount; i++)
                {
                    if (!inFlight.Wait(0, CancellationToken.None))
                    {
                        Interlocked.Increment(ref bucket.Shed);
                        continue;
                    }

                    _ = Task.Run(
                        async () =>
                        {
                            var started = Stopwatch.GetTimestamp();
                            try
                            {
                                var result = await issueCall(cancellationToken);
                                Record(bucket, result, Stopwatch.GetElapsedTime(started));

                                if (!result.Succeeded && result.Status is { } status)
                                {
                                    failuresByStatus.AddOrUpdate(status, 1, (_, count) => count + 1);
                                }
                            }
                            catch (Exception exception)
                            {
                                // A call that throws outside RpcException is still a failed call.
                                // Swallowing it here rather than letting it escape an untracked
                                // Task.Run keeps one bad response from tearing down the run.
                                Record(bucket, new CallResult(false, null), TimeSpan.Zero);
                                failuresByStatus.AddOrUpdate(
                                    StatusCode.Unknown,
                                    1,
                                    (_, count) => count + 1
                                );
                                log($"unexpected exception during load: {exception.Message}");
                            }
                            finally
                            {
                                inFlight.Release();
                            }
                        },
                        CancellationToken.None
                    );
                }
            }
        }

        await DrainAsync(inFlight, options.MaxInFlight, log);

        return LoadReport.From(buckets, failuresByStatus);
    }

    /// <summary>
    /// Waits for every outstanding call by reacquiring the whole semaphore - a permit only comes
    /// back once its call has finished, so holding all of them means nothing is still in flight.
    /// </summary>
    private static async Task DrainAsync(SemaphoreSlim inFlight, int permits, Action<string> log)
    {
        using var drainCts = new CancellationTokenSource(DrainTimeout);

        try
        {
            for (var i = 0; i < permits; i++)
            {
                await inFlight.WaitAsync(drainCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            log(
                $"warning: calls still outstanding after {DrainTimeout.TotalSeconds:F0}s drain window; "
                    + "their outcomes are missing from the timeline"
            );
        }
    }

    private static SecondBucket BucketFor(SecondBucket[] buckets, TimeSpan elapsed)
    {
        var index = Math.Clamp((int)elapsed.TotalSeconds, 0, buckets.Length - 1);
        return buckets[index];
    }

    private static void Record(SecondBucket bucket, CallResult result, TimeSpan latency)
    {
        if (result.Succeeded)
        {
            Interlocked.Increment(ref bucket.Ok);
        }
        else
        {
            Interlocked.Increment(ref bucket.Failed);
        }

        var ticks = latency.Ticks;
        Interlocked.Add(ref bucket.LatencyTicksSum, ticks);

        var observed = Interlocked.Read(ref bucket.MaxLatencyTicks);
        while (ticks > observed)
        {
            var previous = Interlocked.CompareExchange(ref bucket.MaxLatencyTicks, ticks, observed);
            if (previous == observed)
            {
                break;
            }

            observed = previous;
        }
    }

    internal sealed class SecondBucket
    {
        public int Ok;
        public int Failed;
        public int Shed;
        public long LatencyTicksSum;
        public long MaxLatencyTicks;
    }
}

/// <summary>Aggregated result of a load window, plus the per-second timeline used for diagnosis.</summary>
public sealed record LoadReport
{
    public required long Ok { get; init; }
    public required long Failed { get; init; }
    public required long Shed { get; init; }
    public required IReadOnlyDictionary<StatusCode, int> FailuresByStatus { get; init; }
    public required string TimelineCsv { get; init; }

    /// <summary>
    /// Longest run of consecutive seconds in which load was offered and nothing succeeded - the
    /// closest thing here to "how long was it actually down?", and a far more useful number than an
    /// aggregate percentage, which smears a single hard outage across the whole run.
    /// </summary>
    public required int LongestOutageSeconds { get; init; }

    public long Total => Ok + Failed + Shed;

    /// <summary>
    /// P1. Shed calls count against the budget: see the remarks on
    /// <see cref="OpenModelLoadRunner"/>.
    /// </summary>
    public double FailureRate => Total == 0 ? 0.0 : (Failed + Shed) / (double)Total;

    internal static LoadReport From(
        OpenModelLoadRunner.SecondBucket[] buckets,
        IReadOnlyDictionary<StatusCode, int> failuresByStatus
    )
    {
        var csv = new StringBuilder("second,ok,failed,shed,mean_ms,max_ms\n");
        long ok = 0;
        long failed = 0;
        long shed = 0;
        var longestOutage = 0;
        var currentOutage = 0;

        for (var second = 0; second < buckets.Length; second++)
        {
            var bucket = buckets[second];
            var completed = bucket.Ok + bucket.Failed;
            var meanMs =
                completed == 0
                    ? 0.0
                    : TimeSpan.FromTicks(bucket.LatencyTicksSum / completed).TotalMilliseconds;

            csv.Append(CultureInfo.InvariantCulture, $"{second},{bucket.Ok},{bucket.Failed},")
                .Append(CultureInfo.InvariantCulture, $"{bucket.Shed},{meanMs:F1},")
                .Append(CultureInfo.InvariantCulture, $"{TimeSpan.FromTicks(bucket.MaxLatencyTicks).TotalMilliseconds:F1}\n");

            ok += bucket.Ok;
            failed += bucket.Failed;
            shed += bucket.Shed;

            // Seconds where nothing was offered (before the first tick, after the window closes)
            // break the run rather than extending it - an outage means load was flowing and none of
            // it worked.
            var offered = bucket.Ok + bucket.Failed + bucket.Shed;
            if (offered > 0 && bucket.Ok == 0)
            {
                currentOutage++;
                longestOutage = Math.Max(longestOutage, currentOutage);
            }
            else
            {
                currentOutage = 0;
            }
        }

        return new LoadReport
        {
            Ok = ok,
            Failed = failed,
            Shed = shed,
            FailuresByStatus = failuresByStatus,
            TimelineCsv = csv.ToString(),
            LongestOutageSeconds = longestOutage,
        };
    }
}
