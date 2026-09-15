using Amazon.S3;
using CsvHelper;
using Hangfire;
using Microsoft.Extensions.Options;
using Vfps.AppServices;
using Vfps.Config;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.CsvProcessing;

/// <summary>
/// <see cref="PseudonymizationJobDirection.Import"/>: stores already-known original/pseudonym
/// pairs in one namespace, rather than generating pseudonyms for them. The typical source is a
/// <see cref="PseudonymizationJobDirection.Export"/> file from another vfps instance, or a
/// mapping table produced before vfps was introduced.
///
/// The output object is a per-row report rather than a transformed copy of the input: every input
/// column is echoed back unchanged, with a <see cref="StatusColumnName"/> column appended holding
/// that row's <see cref="PseudonymImportOutcome"/>. Rows are never dropped from it, so the report
/// lines up with the input row for row and a rejected row is findable by position even in a file
/// with millions of accepted ones.
/// </summary>
internal sealed class CsvNamespaceImporter(
    IPseudonymAppService pseudonymAppService,
    INamespaceRepository namespaceRepository,
    IAmazonS3 s3,
    IOptions<S3Config> s3Config,
    IOptions<CsvProcessingConfig> csvProcessingConfig,
    CsvJobOutputUploader outputUploader
) : ICsvNamespaceImporter
{
    /// <summary>
    /// The column appended to the output report. Deliberately not configurable - unlike the input
    /// columns, nothing outside this job produces it.
    /// </summary>
    public const string StatusColumnName = "status";

    /// <inheritdoc/>
    public async Task<long> ProcessAsync(
        CsvJobContext context,
        IJobCancellationToken cancellationToken
    )
    {
        using var getResponse = await s3.GetObjectAsync(
            s3Config.Value.Bucket,
            context.Job.InputObjectKey
                ?? throw new InvalidOperationException(
                    $"CSV job '{context.Job.Id}' has no input object to read."
                )
        );
        using var countingStream = new ByteCountingStream(getResponse.ResponseStream);
        context.Progress.BytesProcessed = () => countingStream.BytesRead;

        return await outputUploader.UploadAsync(
            context.OutputObjectKey,
            context.Encoding,
            context.CsvConfig,
            csvWriter => ImportAsync(context, countingStream, csvWriter, cancellationToken),
            cancellationToken.ShutdownToken
        );
    }

    private async Task<long> ImportAsync(
        CsvJobContext context,
        ByteCountingStream countingStream,
        CsvWriter csvWriter,
        IJobCancellationToken cancellationToken
    )
    {
        var job = context.Job;
        var progress = context.Progress;

        // Resolved once up front, exactly as CsvColumnTransformer does - and for the same reason:
        // a job pointing at a namespace that no longer exists should fail immediately rather than
        // many rows in.
        var mapping = PseudonymizationJobAppService.ResolveSingleNamespaceMapping(job);
        var @namespace =
            await namespaceRepository.FindAsync(mapping.Namespace, CancellationToken.None)
            ?? throw new InvalidOperationException(
                $"Namespace '{mapping.Namespace}' does not exist."
            );

        using var reader = new StreamReader(countingStream, context.Encoding);
        using var csvReader = new CsvReader(reader, context.CsvConfig, leaveOpen: true);

        string[]? header = null;
        if (job.HasHeaderRow)
        {
            await csvReader.ReadAsync();
            csvReader.ReadHeader();
            header = csvReader.HeaderRecord;
        }

        var originalValueIndex = CsvJobFormat.ResolveColumnIndex(mapping.SourceColumn, header);
        var pseudonymValueIndex = CsvJobFormat.ResolveColumnIndex(
            mapping.TargetColumn
                ?? throw new InvalidOperationException(
                    "An import job's column mapping must name the pseudonym column."
                ),
            header
        );

        if (header is not null)
        {
            foreach (var h in header)
            {
                csvWriter.WriteField(h);
            }

            csvWriter.WriteField(StatusColumnName);
            await csvWriter.NextRecordAsync();
        }

        // The same batch size as pseudonymization, and for the same reason: one round trip's
        // worth of rows. ImportTrustedBatchAsync costs three round trips per chunk rather than
        // one, so if anything a chunk is worth more here than there.
        var chunkSize = Math.Max(1, csvProcessingConfig.Value.PseudonymizeBatchSize);

        var rows = 0L;
        var totalRowsRead = 0L;
        var chunk = new List<BufferedRow>(chunkSize);
        var phases = context.Phases;

        // Restructured from a `while (await csvReader.ReadAsync())` loop only so the read can be
        // timed separately - see the identical note in CsvColumnTransformer.TransformAsync.
        while (true)
        {
            string?[] rawFields;
            using (phases.Measure(CsvJobPhase.ReadInput))
            {
                if (!await csvReader.ReadAsync())
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var fieldCount = csvReader.Parser.Count;
                rawFields = new string?[fieldCount];
                for (var i = 0; i < fieldCount; i++)
                {
                    rawFields[i] = csvReader.GetField(i);
                }
            }

            chunk.Add(
                new BufferedRow(
                    rawFields,
                    FieldAt(rawFields, originalValueIndex),
                    FieldAt(rawFields, pseudonymValueIndex)
                )
            );
            totalRowsRead++;

            if (chunk.Count >= chunkSize)
            {
                await FlushChunkAsync(@namespace, chunk, csvWriter, progress, phases);
                rows += chunk.Count;
                chunk.Clear();
            }

            if (await progress.MaybeReportAndCheckCancelledAsync(totalRowsRead, rows))
            {
                return rows;
            }
        }

        if (chunk.Count > 0)
        {
            await FlushChunkAsync(@namespace, chunk, csvWriter, progress, phases);
            rows += chunk.Count;
        }

        // Same invariant as CsvColumnTransformer's: every row read has to appear in the report,
        // or a row was silently dropped and the report no longer lines up with the input.
        if (rows != totalRowsRead)
        {
            throw new InvalidOperationException(
                $"Row count mismatch: read {totalRowsRead} row(s) from the input but wrote "
                    + $"{rows} row(s) to the output."
            );
        }

        await progress.ReportAsync(rows);

        return rows;
    }

    /// <summary>
    /// Imports one chunk and writes its report rows. A row whose original or pseudonym cell holds
    /// no value (see <see cref="CsvJobFormat.IsMissingValue"/>) is left out of the batch entirely
    /// rather than rejected by <see cref="IPseudonymAppService.ImportTrustedBatchAsync"/>'s blank
    /// check - it still gets a report row, counted as a missing value the same way every other
    /// direction counts one.
    /// </summary>
    private async Task FlushChunkAsync(
        Namespace @namespace,
        List<BufferedRow> chunk,
        CsvWriter csvWriter,
        CsvJobProgressReporter progress,
        CsvJobPhaseTimer phases
    )
    {
        var entries = new List<PseudonymImportEntry>(chunk.Count);
        var entryRowIndexes = new List<int>(chunk.Count);
        for (var i = 0; i < chunk.Count; i++)
        {
            var row = chunk[i];
            if (IsMissingValue(row.OriginalValue) || IsMissingValue(row.PseudonymValue))
            {
                progress.MissingValueCount++;
                continue;
            }

            entryRowIndexes.Add(i);
            entries.Add(new PseudonymImportEntry(row.OriginalValue, row.PseudonymValue));
        }

        IReadOnlyList<PseudonymImportResult> results;
        using (phases.Measure(CsvJobPhase.ResolveDatabase))
        {
            results = await pseudonymAppService.ImportTrustedBatchAsync(
                @namespace,
                entries,
                CancellationToken.None
            );
        }

        var outcomeByRowIndex = new Dictionary<int, PseudonymImportOutcome>(results.Count);
        for (var i = 0; i < results.Count; i++)
        {
            outcomeByRowIndex[entryRowIndexes[i]] = results[i].Outcome;
        }

        // Same reasoning as CsvColumnTransformer's write scopes: this measures how long the
        // report rows spend waiting on the upload to drain the pipe they are written into.
        using var writeScope = phases.Measure(CsvJobPhase.WriteOutput);

        for (var i = 0; i < chunk.Count; i++)
        {
            foreach (var field in chunk[i].RawFields)
            {
                csvWriter.WriteField(field ?? string.Empty);
            }

            csvWriter.WriteField(
                outcomeByRowIndex.TryGetValue(i, out var outcome)
                    ? outcome.ToString()
                    : MissingValueStatus
            );
            await csvWriter.NextRecordAsync();
        }
    }

    /// <summary>
    /// Reported instead of a <see cref="PseudonymImportOutcome"/> for a row that never reached the
    /// import at all because one of its two cells was blank/a placeholder.
    /// </summary>
    internal const string MissingValueStatus = "MissingValue";

    private static string FieldAt(string?[] rawFields, int index) =>
        index >= 0 && index < rawFields.Length ? rawFields[index] ?? string.Empty : string.Empty;

    private bool IsMissingValue(string raw) =>
        CsvJobFormat.IsMissingValue(raw, csvProcessingConfig.Value);

    private sealed record BufferedRow(
        string?[] RawFields,
        string OriginalValue,
        string PseudonymValue
    );
}
