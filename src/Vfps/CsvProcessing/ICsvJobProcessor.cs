using System.Text;
using CsvHelper.Configuration;
using Hangfire;
using Vfps.Data.Models;

namespace Vfps.CsvProcessing;

/// <summary>
/// One <see cref="PseudonymizationJobDirection"/>'s actual work, once
/// <see cref="CsvPseudonymizationJobRunner"/> has taken care of everything that's the same for
/// all of them: the terminal-state guard, the Running/Completed/Failed transitions, shutdown vs.
/// genuine-failure handling, and the sanitized error message.
///
/// Implementations are resolved from DI per Hangfire job scope. Every one of them produces an
/// output object via <see cref="CsvJobOutputUploader"/> and reports progress via
/// <see cref="CsvJobContext.Progress"/>; what differs is where their rows come from and what
/// happens to each one.
/// </summary>
internal interface ICsvJobProcessor
{
    /// <summary>
    /// Processes the whole job and returns the number of rows written to the output. Returning
    /// early (with fewer rows than the source holds) is how a cooperative cancellation is
    /// reported - see <see cref="CsvJobProgressReporter.MaybeReportAndCheckCancelledAsync"/>.
    /// </summary>
    Task<long> ProcessAsync(CsvJobContext context, IJobCancellationToken cancellationToken);
}

/// <summary>
/// Everything an <see cref="ICsvJobProcessor"/> needs that's derived per job rather than
/// injected: the job itself plus the CSV format and progress bookkeeping
/// <see cref="CsvPseudonymizationJobRunner"/> already built from it, so each processor doesn't
/// re-derive (and risk disagreeing about) any of it.
/// </summary>
/// <param name="Job">The job being processed.</param>
/// <param name="Encoding">Resolved from <see cref="PseudonymizationJob.Encoding"/>.</param>
/// <param name="CsvConfig">
/// Built from the job's delimiter/header settings, with the shared bad-data handler already wired
/// to <paramref name="Progress"/>.
/// </param>
/// <param name="OutputObjectKey">Where this job's output CSV is to be written.</param>
/// <param name="Progress">Shared progress/cancellation bookkeeping for this job.</param>
/// <param name="Phases">
/// Where this job's wall clock goes. Every processor is expected to bracket its reads, its
/// database resolution and its writes with <see cref="CsvJobPhaseTimer.Measure"/> - that split is
/// what distinguishes a job bound by object storage from one bound by the database, which is
/// otherwise indistinguishable from outside.
/// </param>
internal sealed record CsvJobContext(
    PseudonymizationJob Job,
    Encoding Encoding,
    CsvConfiguration CsvConfig,
    string OutputObjectKey,
    CsvJobProgressReporter Progress,
    CsvJobPhaseTimer Phases
);

/// <summary>
/// Transforms an uploaded file's mapped columns in place - see
/// <see cref="PseudonymizationJobDirection.Pseudonymize"/> and
/// <see cref="PseudonymizationJobDirection.Depseudonymize"/>, which share one processor because
/// only what each mapped field resolves to differs between them.
/// </summary>
internal interface ICsvColumnTransformer : ICsvJobProcessor;

/// <summary>
/// Bulk-loads already-known original/pseudonym pairs into one namespace - see
/// <see cref="PseudonymizationJobDirection.Import"/>.
/// </summary>
internal interface ICsvNamespaceImporter : ICsvJobProcessor;

/// <summary>
/// Writes one namespace's stored pseudonyms out as CSV - see
/// <see cref="PseudonymizationJobDirection.Export"/>.
/// </summary>
internal interface ICsvNamespaceExporter : ICsvJobProcessor;
