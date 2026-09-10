namespace Vfps;

/// <summary>
/// The Hangfire queue names this app uses. Named constants rather than string literals because the
/// enqueue side and the server's <c>Queues</c> list have to agree exactly - a typo on either side
/// doesn't fail, it just leaves jobs sitting unprocessed forever.
/// </summary>
public static class HangfireQueues
{
    /// <summary>
    /// Hangfire's own default queue, which is where anything enqueued without an explicit queue
    /// lands - CSV pseudonymization jobs, in this app. Served only by instances configured to
    /// process them (see <see cref="Config.CsvProcessingConfig.ProcessJobs"/>).
    ///
    /// The literal has to stay "default": it's Hangfire's built-in fallback for an unqualified
    /// enqueue, not a name this app is free to choose.
    /// </summary>
    public const string Default = "default";

    /// <summary>
    /// Short, cheap, periodic housekeeping that every instance is willing to run - currently just
    /// the pseudonym-count recompute. Separate from <see cref="Default"/> so that serving it
    /// everywhere doesn't drag CSV jobs onto instances meant only to accept them.
    /// </summary>
    public const string Metrics = "metrics";
}
