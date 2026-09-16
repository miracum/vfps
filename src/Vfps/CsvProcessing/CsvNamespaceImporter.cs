using Amazon.S3;
using CsvHelper;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vfps.AppServices;
using Vfps.Config;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.CsvProcessing;

/// <summary>
/// <see cref="PseudonymizationJobDirection.Import"/>: stores already-known original/pseudonym
/// pairs, rather than generating pseudonyms for them. The typical source is a
/// <see cref="PseudonymizationJobDirection.Export"/> file from another vfps instance, or a
/// mapping table produced before vfps was introduced.
///
/// Every pair goes into the single namespace the job names, unless the job also names a namespace
/// column (<see cref="ColumnMapping.NamespaceColumn"/>) - then each row carries its own target
/// namespace and one file can populate many at once, which is what makes a whole instance's export
/// loadable in one job instead of one per namespace.
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
    CsvJobOutputUploader outputUploader,
    ILogger<CsvNamespaceImporter> logger
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
        // Resuming rather than a plain GetObjectAsync: this stream stays open for the whole job,
        // read at the pace of the database work between chunks, which is long enough for an idle
        // connection to be reaped underneath it - see ResumingS3ObjectStream.
        using var countingStream = await ResumingS3ObjectStream.OpenAsync(
            s3,
            s3Config.Value.Bucket,
            context.Job.InputObjectKey
                ?? throw new InvalidOperationException(
                    $"CSV job '{context.Job.Id}' has no input object to read."
                ),
            logger,
            cancellationToken.ShutdownToken
        );
        context.Progress.BytesProcessed = () => countingStream.BytesRead;

        try
        {
            return await outputUploader.UploadAsync(
                context.OutputObjectKey,
                context.Encoding,
                context.CsvConfig,
                csvWriter => ImportAsync(context, countingStream, csvWriter, cancellationToken),
                cancellationToken.ShutdownToken
            );
        }
        finally
        {
            // In a finally so an interrupted job still reports it - a job that died part-way is
            // exactly when knowing whether it was starved of bytes or of CPU is worth most.
            context.Phases.AddInputFetch(countingStream.TimeFetching);
        }
    }

    private async Task<long> ImportAsync(
        CsvJobContext context,
        Stream countingStream,
        CsvWriter csvWriter,
        IJobCancellationToken cancellationToken
    )
    {
        var job = context.Job;
        var progress = context.Progress;

        var mapping = PseudonymizationJobAppService.ResolveSingleNamespaceMapping(job);
        var usesNamespaceColumn = !string.IsNullOrEmpty(mapping.NamespaceColumn);

        // Resolved once up front, exactly as CsvColumnTransformer does - and for the same reason:
        // a job pointing at a namespace that no longer exists should fail immediately rather than
        // many rows in. That reasoning only holds for a namespace the *job* names: one a row names
        // is data, and a single bad row must not take a million-row file down with it, so those
        // are resolved lazily below and reported per row instead.
        var fixedNamespace = usesNamespaceColumn
            ? null
            : await namespaceRepository.FindAsync(mapping.Namespace, CancellationToken.None)
                ?? throw new InvalidOperationException(
                    $"Namespace '{mapping.Namespace}' does not exist."
                );

        // Namespaces named by rows, resolved once each for the whole job rather than per row -
        // the same saving the transform path gets from resolving its mappings' namespaces up
        // front. A name that does not exist is cached as null too, so a typo repeated on a
        // million rows costs one lookup rather than a million.
        var namespacesByName = new Dictionary<string, Namespace?>(StringComparer.Ordinal);

        using var reader = CsvJobFormat.CreateReader(countingStream, context.Encoding);
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
        var namespaceIndex = usesNamespaceColumn
            ? CsvJobFormat.ResolveColumnIndex(mapping.NamespaceColumn!, header)
            : -1;

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
            // Brackets fetching and parsing together; the fetch half is deducted at flush from
            // what ResumingS3ObjectStream measured inside these same reads - see
            // CsvJobPhase.ParseInput.
            using (phases.Measure(CsvJobPhase.ParseInput))
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
                    FieldAt(rawFields, pseudonymValueIndex),
                    FieldAt(rawFields, namespaceIndex)
                )
            );
            totalRowsRead++;

            if (chunk.Count >= chunkSize)
            {
                await FlushChunkAsync(
                    fixedNamespace,
                    namespacesByName,
                    chunk,
                    csvWriter,
                    progress,
                    phases
                );
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
            await FlushChunkAsync(
                fixedNamespace,
                namespacesByName,
                chunk,
                csvWriter,
                progress,
                phases
            );
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
    /// Imports one chunk and writes its report rows.
    ///
    /// A row whose original or pseudonym cell holds no value (see
    /// <see cref="CsvJobFormat.IsMissingValue"/>) is left out of the batch entirely rather than
    /// rejected by <see cref="IPseudonymAppService.ImportTrustedBatchAsync"/>'s blank check - it
    /// still gets a report row, counted as a missing value the same way every other direction
    /// counts one. A namespace-column job treats a blank namespace cell the same way: there is no
    /// default to fall back to, and quietly putting a row in some other namespace is the one
    /// outcome a pseudonymization service must never produce.
    ///
    /// Rows are grouped by target namespace and imported one batch per namespace, because
    /// <see cref="IPseudonymAppService.ImportTrustedBatchAsync"/>'s round trips are all
    /// namespace-scoped. A single-namespace job therefore behaves exactly as it did before this
    /// grew a namespace column - one batch - while a namespace-column job costs one batch per
    /// distinct namespace appearing in the chunk.
    /// </summary>
    private async Task FlushChunkAsync(
        Namespace? fixedNamespace,
        Dictionary<string, Namespace?> namespacesByName,
        List<BufferedRow> chunk,
        CsvWriter csvWriter,
        CsvJobProgressReporter progress,
        CsvJobPhaseTimer phases
    )
    {
        var batches = new Dictionary<string, NamespaceBatch>(StringComparer.Ordinal);
        var statusByRowIndex = new Dictionary<int, string>(chunk.Count);

        using (phases.Measure(CsvJobPhase.ResolveDatabase))
        {
            for (var i = 0; i < chunk.Count; i++)
            {
                var row = chunk[i];
                if (IsMissingValue(row.OriginalValue) || IsMissingValue(row.PseudonymValue))
                {
                    progress.MissingValueCount++;
                    statusByRowIndex[i] = MissingValueStatus;
                    continue;
                }

                var @namespace = fixedNamespace;
                if (@namespace is null)
                {
                    if (IsMissingValue(row.NamespaceName))
                    {
                        progress.MissingValueCount++;
                        statusByRowIndex[i] = MissingValueStatus;
                        continue;
                    }

                    @namespace = await ResolveNamespaceAsync(row.NamespaceName, namespacesByName);
                    if (@namespace is null)
                    {
                        statusByRowIndex[i] = UnknownNamespaceStatus;
                        continue;
                    }
                }

                if (!batches.TryGetValue(@namespace.Name, out var batch))
                {
                    batch = new NamespaceBatch(@namespace);
                    batches[@namespace.Name] = batch;
                }

                batch.RowIndexes.Add(i);
                batch.Entries.Add(new PseudonymImportEntry(row.OriginalValue, row.PseudonymValue));
            }

            foreach (var batch in batches.Values)
            {
                var results = await pseudonymAppService.ImportTrustedBatchAsync(
                    batch.Namespace,
                    batch.Entries,
                    CancellationToken.None
                );

                // Positional, as that method's contract promises: one result per entry, in order.
                for (var i = 0; i < results.Count; i++)
                {
                    statusByRowIndex[batch.RowIndexes[i]] = results[i].Outcome.ToString();
                }
            }
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

            // Indexed rather than looked up defensively: every row above lands in exactly one of
            // the branches that assigns a status, so a miss here is a bug in that logic and
            // should fail the job loudly rather than write a plausible-looking wrong one.
            csvWriter.WriteField(statusByRowIndex[i]);
            await csvWriter.NextRecordAsync();
        }
    }

    /// <summary>
    /// Looks a row-named namespace up at most once per job, caching misses as well as hits.
    /// </summary>
    private async Task<Namespace?> ResolveNamespaceAsync(
        string namespaceName,
        Dictionary<string, Namespace?> namespacesByName
    )
    {
        if (namespacesByName.TryGetValue(namespaceName, out var cached))
        {
            return cached;
        }

        var resolved = await namespaceRepository.FindAsync(namespaceName, CancellationToken.None);
        namespacesByName[namespaceName] = resolved;

        return resolved;
    }

    /// <summary>One namespace's share of a chunk, and which rows of it they came from.</summary>
    private sealed class NamespaceBatch(Namespace @namespace)
    {
        public Namespace Namespace { get; } = @namespace;
        public List<PseudonymImportEntry> Entries { get; } = [];
        public List<int> RowIndexes { get; } = [];
    }

    /// <summary>
    /// Reported for a row naming a namespace that does not exist - only reachable for a job whose
    /// namespace comes from a column, where the name is data rather than job configuration. A job
    /// naming a missing namespace itself fails outright instead, up front.
    /// </summary>
    internal const string UnknownNamespaceStatus = "UnknownNamespace";

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
        string PseudonymValue,
        // Empty unless the job names a namespace column - see CsvNamespaceImporter's summary.
        string NamespaceName
    );
}
