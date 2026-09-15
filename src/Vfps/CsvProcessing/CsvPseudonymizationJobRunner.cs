using System.Text;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Vfps.AppServices;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.CsvProcessing;

/// <inheritdoc cref="ICsvPseudonymizationJobRunner"/>
// Internal, unlike the interface Hangfire enqueues against: nothing outside this assembly
// constructs the runner, and its constructor takes the (internal) per-direction processors.
internal sealed class CsvPseudonymizationJobRunner(
    IPseudonymizationJobRepository jobRepository,
    ICsvColumnTransformer columnTransformer,
    ICsvNamespaceImporter namespaceImporter,
    ICsvNamespaceExporter namespaceExporter,
    ILogger<CsvPseudonymizationJobRunner> logger
) : ICsvPseudonymizationJobRunner
{
    /// <inheritdoc/>
    public async Task RunAsync(
        Guid jobId,
        string jobLabel,
        IJobCancellationToken cancellationToken,
        PerformContext? context = null
    )
    {
        var job =
            await jobRepository.FindAsync(jobId, CancellationToken.None)
            ?? throw new InvalidOperationException(
                $"Pseudonymization job '{jobId}' does not exist."
            );

        // Guards against a manual re-run (e.g. via the Hangfire dashboard) of a job that's
        // already reached a genuinely terminal state - automatic exception-triggered retries are
        // disabled (see Program.cs), but nothing stops an operator from clicking "Retry" there
        // directly.
        //
        // Stalled is deliberately NOT included here, even though it looks terminal in the UI.
        // StalledPseudonymizationJobWatchdogService sets it purely on a stale-LastUpdatedAt
        // heuristic - the runner that was processing this job is presumed dead (e.g. its pod
        // crashed). The actual recovery for that is Hangfire's own dead-server detection, a
        // separate mechanism with its own independent timeout, which re-dispatches the same
        // underlying Hangfire job to a live server once it notices. That dead-server timeout and
        // this job's stalled threshold (CsvProcessingConfig.StalledJobThreshold) aren't
        // coordinated, so there's no guarantee which fires first. Treating Stalled as terminal
        // here would mean: whenever the stall watchdog happens to win that race, Hangfire's
        // legitimate re-dispatch arrives to find the job already "terminal" and silently no-ops -
        // permanently stranding it with no automatic recovery at all, and no path forward short of
        // an operator noticing and submitting a brand new job.
        if (
            job.Status
            is PseudonymizationJobStatus.Cancelled
                or PseudonymizationJobStatus.Completed
                or PseudonymizationJobStatus.Failed
        )
        {
            return;
        }

        // Visible on this job's own Hangfire Dashboard page (under "Parameters") - lets an
        // operator see which S3 objects a stuck/failed job was reading from and writing to
        // without needing separate access to vfps's own database.
        context?.SetJobParameter("InputObjectKey", job.InputObjectKey);

        await jobRepository.UpdateStatusAsync(
            jobId,
            PseudonymizationJobStatus.Running,
            null,
            CancellationToken.None
        );

        try
        {
            var (outputObjectKey, rowsProcessed) = await ProcessAsync(
                job,
                cancellationToken,
                context
            );

            // A cooperative cancel may have landed while the last chunk was still uploading -
            // don't overwrite Cancelled with Completed. Same for Stalled:
            // StalledPseudonymizationJobWatchdogService runs independently of this runner and can
            // mark a job Stalled (a heuristic guess, occasionally a false positive - this job
            // looked dead but was actually still alive) at any point, including while ProcessAsync
            // above was still finishing up - that verdict shouldn't be silently overwritten by a
            // late success either, so the discrepancy stays visible instead of being hidden.
            var current = await jobRepository.FindAsync(jobId, CancellationToken.None);
            if (
                current?.Status
                is not (
                    PseudonymizationJobStatus.Cancelled
                    or PseudonymizationJobStatus.Failed
                    or PseudonymizationJobStatus.Stalled
                )
            )
            {
                await jobRepository.CompleteAsync(
                    jobId,
                    outputObjectKey,
                    rowsProcessed,
                    CancellationToken.None
                );
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.ShutdownToken.IsCancellationRequested)
        {
            // The Hangfire *server* is shutting down (e.g. a pod restart/rolling upgrade,
            // resource-limit change, or a rollout) - ThrowIfCancellationRequested() throws this
            // the moment that happens, mid-row. This is not a genuine processing failure, so
            // don't mark the job Failed here: leave its Status as Running (already set above) so
            // Hangfire's own dead-server recovery re-dispatches this same job once a server is
            // available again, and RunAsync's own terminal-state guard doesn't turn that
            // re-dispatch into a silent no-op. Marking it Failed here was the actual root cause of
            // a real incident - the job kept running server-side right up to the restart, Hangfire
            // itself never counted this as a failure (it has this exact same special case
            // internally) and later re-ran the job, which then immediately no-opped against the
            // Failed status this catch used to set and reported back to Hangfire as "Succeeded" -
            // while vfps's own UI kept showing Failed, and the job's real output was incomplete.
            logger.LogWarning(
                "CSV pseudonymization job {JobId} was interrupted by a server shutdown mid-"
                    + "processing - it will be reprocessed once a Hangfire server is available "
                    + "again.",
                jobId
            );
            throw;
        }
        catch (Exception ex)
        {
            // Never persist raw row content or the raw exception string here - this service's
            // entire purpose is protecting the values that would otherwise leak into this field.
            logger.LogError(ex, "CSV pseudonymization job {JobId} failed", jobId);
            await jobRepository.UpdateStatusAsync(
                jobId,
                PseudonymizationJobStatus.Failed,
                "Processing failed - see server logs for details.",
                CancellationToken.None
            );
            throw;
        }
    }

    /// <summary>
    /// Everything that's the same for every <see cref="PseudonymizationJobDirection"/>: the output
    /// object's (deterministic, job-id-derived) key, the encoding and CSV format derived from the
    /// job's own settings, and the progress bookkeeping - then hands the actual work to the
    /// processor for this job's direction.
    /// </summary>
    private async Task<(string OutputObjectKey, long RowsProcessed)> ProcessAsync(
        PseudonymizationJob job,
        IJobCancellationToken cancellationToken,
        PerformContext? context
    )
    {
        var outputObjectKey =
            $"{PseudonymizationJobAppService.S3ObjectKeyPrefix}{job.Id}/output.csv";
        // Set as soon as the (deterministic, jobId-derived) path is known, not only on success -
        // this is the path the runner is writing to/intends to write to, not a "job is done"
        // signal (that's what the job's own Status is for).
        context?.SetJobParameter("OutputObjectKey", outputObjectKey);

        var progress = new CsvJobProgressReporter(jobRepository, job.Id);
        var jobContext = new CsvJobContext(
            job,
            Encoding.GetEncoding(job.Encoding),
            CsvJobFormat.CreateConfiguration(job, progress, logger),
            outputObjectKey,
            progress
        );

        var rowsProcessed = await ProcessorFor(job.Direction)
            .ProcessAsync(jobContext, cancellationToken);

        return (outputObjectKey, rowsProcessed);
    }

    private ICsvJobProcessor ProcessorFor(PseudonymizationJobDirection direction) =>
        direction switch
        {
            PseudonymizationJobDirection.Import => namespaceImporter,
            PseudonymizationJobDirection.Export => namespaceExporter,
            // Pseudonymize and Depseudonymize both transform an uploaded file's columns in place;
            // which of the two it is only changes what each mapped field resolves to, which the
            // transformer reads off the job itself.
            _ => columnTransformer,
        };
}
