using CsvHelper;
using Hangfire;
using Vfps.AppServices;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.CsvProcessing;

/// <summary>
/// <see cref="PseudonymizationJobDirection.Export"/>: writes every pseudonym stored in one
/// namespace out as a two-column CSV (original value, pseudonym value), ready to be loaded into
/// another instance by <see cref="CsvNamespaceImporter"/>.
///
/// The only direction with no input file: rows come from the database instead, walked with the
/// same keyset pagination the List RPC uses (see
/// <see cref="IPseudonymRepository.ListByNamespaceAsync"/>) so a namespace of any size streams out
/// at constant cost per page, rather than OFFSET's cost that grows with page depth.
/// </summary>
internal sealed class CsvNamespaceExporter(
    IPseudonymRepository pseudonymRepository,
    INamespaceRepository namespaceRepository,
    CsvJobOutputUploader outputUploader
) : ICsvNamespaceExporter
{
    // Rows fetched per round trip. Not tied to CsvProcessingConfig.PseudonymizeBatchSize: that one
    // sizes a *write* batch against the upsert's own cost curve, while this is a plain indexed
    // read whose rows are handed straight to the CSV writer and dropped. Fixed at a value that
    // keeps a page's worth of rows comfortably small in memory while making the per-round-trip
    // overhead negligible.
    private const int PageSize = 1000;

    /// <inheritdoc/>
    public async Task<long> ProcessAsync(
        CsvJobContext context,
        IJobCancellationToken cancellationToken
    )
    {
        var mapping = PseudonymizationJobAppService.ResolveSingleNamespaceMapping(context.Job);

        // Checked up front purely so an export of a namespace that no longer exists fails with
        // that reason rather than silently completing as an empty file, which is exactly what an
        // empty namespace legitimately produces.
        _ =
            await namespaceRepository.FindAsync(mapping.Namespace, CancellationToken.None)
            ?? throw new InvalidOperationException(
                $"Namespace '{mapping.Namespace}' does not exist."
            );

        // Left at its default of 0 bytes read: there is no input object to have consumed any. The
        // job's TotalBytes is 0 for the same reason, so the UI reports rows only - see
        // CsvJobProgressReporter.BytesProcessed.
        return await outputUploader.UploadAsync(
            context.OutputObjectKey,
            context.Encoding,
            context.CsvConfig,
            csvWriter => ExportAsync(context, mapping, csvWriter, cancellationToken),
            cancellationToken.ShutdownToken
        );
    }

    private async Task<long> ExportAsync(
        CsvJobContext context,
        ColumnMapping mapping,
        CsvWriter csvWriter,
        IJobCancellationToken cancellationToken
    )
    {
        var progress = context.Progress;
        var phases = context.Phases;

        if (context.Job.HasHeaderRow)
        {
            using var headerScope = phases.Measure(CsvJobPhase.WriteOutput);

            csvWriter.WriteField(mapping.SourceColumn);
            csvWriter.WriteField(mapping.TargetColumn ?? string.Empty);
            await csvWriter.NextRecordAsync();
        }

        var rows = 0L;
        PseudonymPageCursor? cursor = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<Pseudonym> page;
            using (phases.Measure(CsvJobPhase.ResolveDatabase))
            {
                page = await pseudonymRepository.ListByNamespaceAsync(
                    mapping.Namespace,
                    cursor,
                    PageSize,
                    CancellationToken.None
                );
            }

            foreach (var pseudonym in page)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Scoped per row rather than around the whole page, unlike every other direction:
                // this loop interleaves writes with progress check-ins, so one scope spanning the
                // page would bill those database round trips to WriteOutput as well.
                using (phases.Measure(CsvJobPhase.WriteOutput))
                {
                    csvWriter.WriteField(pseudonym.OriginalValue);
                    csvWriter.WriteField(pseudonym.PseudonymValue);
                    await csvWriter.NextRecordAsync();
                }

                rows++;

                // Rows are written as they're read rather than buffered into a chunk, so read and
                // written counts are the same number here - unlike the directions that flush a
                // chunk at a time.
                if (await progress.MaybeReportAndCheckCancelledAsync(rows, rows))
                {
                    return rows;
                }
            }

            // Same "did we get a full page" heuristic as PseudonymAppService.ListAsync, including
            // its one wasted round trip when the total happens to be an exact multiple of the
            // page size.
            if (page.Count < PageSize)
            {
                break;
            }

            var last = page[^1];
            cursor = new PseudonymPageCursor(
                last.CreatedAt,
                last.OriginalValue,
                last.SequenceNumber
            );
        }

        await progress.ReportAsync(rows);

        return rows;
    }
}
