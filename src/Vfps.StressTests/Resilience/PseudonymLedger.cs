namespace Vfps.StressTests.Resilience;

/// <summary>A pseudonym exactly as the service handed it back during the load window.</summary>
public readonly record struct PseudonymPair(string OriginalValue, string PseudonymValue);

/// <summary>
/// A bounded, uniformly-sampled record of what the service promised callers while chaos was running.
/// This is the whole point of the resilience test: <c>Create</c> is idempotent, so a failover that
/// loses committed transactions never surfaces as an error - it surfaces later as a different
/// pseudonym for the same original value. Nothing catches that except comparing against what was
/// actually returned at the time.
/// </summary>
/// <remarks>
/// Uses Algorithm R reservoir sampling rather than keeping the first or last N pairs, because the
/// sample has to span the whole run: a sample biased to the start would miss writes made during a
/// failover, and one biased to the end would miss the writes most likely to have been lost by it.
/// </remarks>
public sealed class PseudonymLedger(int capacity)
{
    private readonly Lock gate = new();
    private readonly List<PseudonymPair> reservoir = new(capacity);
    private readonly Random random = new();
    private long seen;

    /// <summary>Total successful creations observed, whether or not they were retained.</summary>
    public long TotalSeen => Interlocked.Read(ref seen);

    public void Offer(string originalValue, string pseudonymValue)
    {
        var index = Interlocked.Increment(ref seen) - 1;

        lock (gate)
        {
            if (reservoir.Count < capacity)
            {
                reservoir.Add(new PseudonymPair(originalValue, pseudonymValue));
                return;
            }

            // Algorithm R: the n-th item (0-based) replaces a uniformly chosen existing entry with
            // probability capacity/(n+1), which leaves every observed pair equally likely to be held.
            var candidate = random.NextInt64(index + 1);
            if (candidate < capacity)
            {
                reservoir[(int)candidate] = new PseudonymPair(originalValue, pseudonymValue);
            }
        }
    }

    public IReadOnlyList<PseudonymPair> Snapshot()
    {
        lock (gate)
        {
            return [.. reservoir];
        }
    }
}
