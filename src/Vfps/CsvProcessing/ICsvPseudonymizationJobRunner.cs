using Hangfire;
using Hangfire.Server;

namespace Vfps.CsvProcessing;

/// <summary>
/// Executes one CSV pseudonymization job (see <see cref="Data.Models.PseudonymizationJob"/>).
/// Registered scoped and invoked by Hangfire, which creates its own DI scope per job execution -
/// entirely independent of any Blazor circuit's scope.
/// </summary>
public interface ICsvPseudonymizationJobRunner
{
    /// <summary>
    /// <paramref name="jobLabel"/> is never read by this method itself - it exists purely so
    /// Hangfire's dashboard can show something more legible than "RunAsync" in the jobs list
    /// (see the <see cref="JobDisplayNameAttribute"/> below, which formats it in via "{1}", i.e.
    /// this parameter's position). Build it from the job's Direction/OriginalFileName at the
    /// enqueue call site (see PseudonymizationJobAppService.MarkUploadCompleteAsync) - by the time
    /// this method runs, that's already redundant with <paramref name="jobId"/>'s own row in the
    /// database, but Hangfire's dashboard has no access to vfps's own database to look it up
    /// itself, only whatever was serialized as an argument at enqueue time.
    /// <paramref name="cancellationToken"/> is a Hangfire <see cref="IJobCancellationToken"/>,
    /// not a plain <see cref="CancellationToken"/> - pass <see cref="JobCancellationToken.Null"/>
    /// at the enqueue call site; Hangfire substitutes the real token at execution time.
    /// <paramref name="context"/> is likewise a Hangfire-injected special parameter type - pass
    /// null at the enqueue call site (matching <see cref="JobCancellationToken.Null"/>'s own
    /// idiom for the same reason: Hangfire discards whatever's passed there and substitutes its
    /// own real, non-null instance before invoking this method). Used to surface the S3 input/
    /// output object keys as job parameters on the job's Hangfire Dashboard page, for an operator
    /// debugging a stuck/failed job without needing separate access to vfps's own database.
    /// </summary>
    [JobDisplayName("{1}")]
    Task RunAsync(
        Guid jobId,
        string jobLabel,
        IJobCancellationToken cancellationToken,
        PerformContext? context = null
    );
}
