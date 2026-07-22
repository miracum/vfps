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
    Task RunAsync(
        Guid jobId,
        IJobCancellationToken cancellationToken,
        PerformContext? context = null
    );
}
