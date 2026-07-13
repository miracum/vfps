using Hangfire;

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
    /// </summary>
    Task RunAsync(Guid jobId, IJobCancellationToken cancellationToken);
}
