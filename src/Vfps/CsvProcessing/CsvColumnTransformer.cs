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
/// The original two CSV job directions: reads the input file row by row (via
/// <c>GetField(index)</c> only - never a typed <c>GetField&lt;T&gt;()</c>, since fields here are
/// opaque values to relocate, not data to interpret), replaces or appends the mapped columns'
/// values, and writes the result out. See
/// <see cref="PseudonymizationJobDirection.Pseudonymize"/> and
/// <see cref="PseudonymizationJobDirection.Depseudonymize"/>.
/// </summary>
internal sealed class CsvColumnTransformer(
    IPseudonymAppService pseudonymAppService,
    INamespaceRepository namespaceRepository,
    IAmazonS3 s3,
    IOptions<S3Config> s3Config,
    IOptions<CsvProcessingConfig> csvProcessingConfig,
    CsvJobOutputUploader outputUploader,
    ILogger<CsvColumnTransformer> logger
) : ICsvColumnTransformer
{
    // Depseudonymize resolves a chunk via one concurrent DB call per row (Task.WhenAll - see
    // FlushChunkDepseudonymizeAsync), each grabbing its own pooled connection, so this bound
    // exists purely to protect the connection pool: kept well under its "Maximum Pool Size=50"
    // default (see appsettings.Development.json/compose.yaml) so one CSV job can't starve every
    // other Hangfire worker or request of a connection. Deliberately a constant, not configurable
    // like Pseudonymize's batch size below - an operator raising it would need to reason about
    // every other consumer of that same shared connection pool, not just this one job type.
    // Pseudonymize resolves a whole chunk via a single batched upsert round trip regardless of
    // chunk size, so it never touches more than one connection at a time and this concern doesn't
    // apply to it - see CsvProcessingConfig.PseudonymizeBatchSize for that one's own reasoning.
    private const int DepseudonymizeConcurrencyChunkSize = 20;

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
                csvWriter => TransformAsync(context, countingStream, csvWriter, cancellationToken),
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

    /// <summary>
    /// Reads+transforms the input CSV row by row and writes the result into
    /// <paramref name="csvWriter"/>, which <see cref="CsvJobOutputUploader"/> concurrently uploads
    /// from the other end of a pipe.
    /// </summary>
    private async Task<long> TransformAsync(
        CsvJobContext context,
        Stream countingStream,
        CsvWriter csvWriter,
        IJobCancellationToken cancellationToken
    )
    {
        var job = context.Job;
        var progress = context.Progress;

        using var reader = CsvJobFormat.CreateReader(countingStream, context.Encoding);
        using var csvReader = new CsvReader(reader, context.CsvConfig, leaveOpen: true);

        string[]? header = null;
        if (job.HasHeaderRow)
        {
            await csvReader.ReadAsync();
            csvReader.ReadHeader();
            header = csvReader.HeaderRecord;
        }

        // Resolve every distinct namespace this job's column mappings reference exactly
        // once, up front - not on every field of every row, which used to be the dominant
        // per-row cost (a namespace lookup on top of the actual upsert/reverse-lookup, for
        // every single value). Also fails the job immediately if a mapping references a
        // namespace that no longer exists, rather than only discovering that many rows in.
        var namespaces = new Dictionary<string, Namespace>();
        foreach (var namespaceName in job.ColumnMappings.Select(m => m.Namespace).Distinct())
        {
            namespaces[namespaceName] =
                await namespaceRepository.FindAsync(namespaceName, CancellationToken.None)
                ?? throw new InvalidOperationException(
                    $"Namespace '{namespaceName}' does not exist."
                );
        }

        var inPlaceBySourceIndex = new Dictionary<int, Namespace>();
        var appended = new List<(int SourceIndex, string TargetColumn, Namespace Namespace)>();
        foreach (var mapping in job.ColumnMappings)
        {
            var sourceIndex = CsvJobFormat.ResolveColumnIndex(mapping.SourceColumn, header);
            var @namespace = namespaces[mapping.Namespace];
            if (mapping.TargetColumn is null)
            {
                inPlaceBySourceIndex.TryAdd(sourceIndex, @namespace);
            }
            else
            {
                appended.Add((sourceIndex, mapping.TargetColumn, @namespace));
            }
        }

        if (header is not null)
        {
            foreach (var h in header)
            {
                csvWriter.WriteField(h);
            }

            foreach (var a in appended)
            {
                csvWriter.WriteField(a.TargetColumn);
            }

            await csvWriter.NextRecordAsync();
        }

        var chunkSize =
            job.Direction == PseudonymizationJobDirection.Pseudonymize
                // Clamped rather than trusted as-is - a misconfigured 0 or negative value
                // would otherwise throw out of the List<BufferedRow> capacity below or flush
                // on every single row.
                ? Math.Max(1, csvProcessingConfig.Value.PseudonymizeBatchSize)
                : DepseudonymizeConcurrencyChunkSize;

        // rows: actually flushed/written so far - what's reported as progress and eventually
        // RowsProcessed. totalRowsRead: consumed from the reader so far, flushed or not - this
        // drives when MaybeReportAndCheckCancelledAsync actually checks in, decoupled
        // from the (now potentially much larger, for Pseudonymize) flush boundary so a big
        // batch size can't blunt cancellation/progress responsiveness. If a cancellation is
        // noticed while a batch is only partially buffered, that partial buffer is simply
        // dropped rather than flushed - same "discard whatever hasn't been written yet"
        // behavior as always, just checked more often than the flush boundary now allows for.
        var rows = 0L;
        var totalRowsRead = 0L;
        var chunk = new List<BufferedRow>(chunkSize);
        var phases = context.Phases;

        // Rewritten from `while (await csvReader.ReadAsync())` purely so the read can be timed:
        // parsing is where this job waits on the input object's bytes, and lumping it in with the
        // work done per row afterwards would hide whether a slow job is slow because S3 is not
        // delivering. Same structure in CsvNamespaceImporter, for the same reason.
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

            chunk.Add(new BufferedRow(rawFields));
            totalRowsRead++;

            if (chunk.Count >= chunkSize)
            {
                await FlushChunkAsync(
                    chunk,
                    job.Direction,
                    inPlaceBySourceIndex,
                    appended,
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
                chunk,
                job.Direction,
                inPlaceBySourceIndex,
                appended,
                csvWriter,
                progress,
                phases
            );
            rows += chunk.Count;
        }

        // Every row consumed from the input must end up written to the output - a
        // (de-)pseudonymized file with a different row count than its input would mean rows
        // were silently dropped or duplicated somewhere in the chunking/flush logic above.
        // That's a correctness bug serious enough to fail the job over rather than complete
        // it and let a caller unknowingly rely on a truncated/corrupted file. Only reached on
        // a natural end-of-input - the early `return rows` above on cancellation is
        // intentionally exempt, since fewer output rows than input rows is expected there.
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
    /// Writes out every row in <paramref name="chunk"/>, resolving each field first. Pseudonymize
    /// and Depseudonymize deliberately take different paths here (see
    /// <see cref="FlushChunkPseudonymizeAsync"/> and <see cref="FlushChunkDepseudonymizeAsync"/>)
    /// rather than a single generic one - only Pseudonymize's per-value operation (an upsert) can
    /// be batched into one round trip; Depseudonymize's (a lookup) cannot use the same batched SQL
    /// shape, so it keeps the one-DB-call-per-value-but-concurrent-across-the-chunk approach.
    /// </summary>
    private async Task FlushChunkAsync(
        List<BufferedRow> chunk,
        PseudonymizationJobDirection direction,
        Dictionary<int, Namespace> inPlaceBySourceIndex,
        List<(int SourceIndex, string TargetColumn, Namespace Namespace)> appended,
        CsvWriter csvWriter,
        CsvJobProgressReporter progress,
        CsvJobPhaseTimer phases
    )
    {
        if (direction == PseudonymizationJobDirection.Pseudonymize)
        {
            await FlushChunkPseudonymizeAsync(
                chunk,
                inPlaceBySourceIndex,
                appended,
                csvWriter,
                progress,
                phases
            );
        }
        else
        {
            await FlushChunkDepseudonymizeAsync(
                chunk,
                direction,
                inPlaceBySourceIndex,
                appended,
                csvWriter,
                progress,
                phases
            );
        }
    }

    /// <summary>
    /// Pseudonymize fast path: every field needing a pseudonym across the whole chunk - every row,
    /// every in-place and appended mapping, regardless of how many distinct namespaces are
    /// involved - is collected into one list and resolved via a single
    /// <see cref="IPseudonymAppService.CreateTrustedBatchAsync"/> round trip, rather than one
    /// upsert round trip per field per row. That per-value round trip was the dominant cost of
    /// processing a CSV job; concurrency across a chunk (the previous approach, still used by
    /// <see cref="FlushChunkDepseudonymizeAsync"/>) only overlaps that cost, batching removes most
    /// of it outright.
    /// </summary>
    private async Task FlushChunkPseudonymizeAsync(
        List<BufferedRow> chunk,
        Dictionary<int, Namespace> inPlaceBySourceIndex,
        List<(int SourceIndex, string TargetColumn, Namespace Namespace)> appended,
        CsvWriter csvWriter,
        CsvJobProgressReporter progress,
        CsvJobPhaseTimer phases
    )
    {
        // A blank cell (or a common "no value" placeholder - see CsvJobFormat.IsMissingValue)
        // can't be pseudonymized, and CreateTrustedBatchAsync rejects one outright - excluded here
        // rather than letting that exception fail the whole job/chunk over what a real-world CSV
        // export routinely contains. Left out of the batch entirely (not just skipped on write) so
        // it never occupies an upsert slot or a place in the resolved dictionary below.
        var requests = new List<(Namespace Namespace, string OriginalValue)>();
        foreach (var row in chunk)
        {
            foreach (var (sourceIndex, ns) in inPlaceBySourceIndex)
            {
                var raw = row.RawFields[sourceIndex] ?? string.Empty;
                if (!IsMissingValue(raw))
                {
                    requests.Add((ns, raw));
                }
            }

            foreach (var mapping in appended)
            {
                var raw =
                    mapping.SourceIndex < row.RawFields.Length
                        ? row.RawFields[mapping.SourceIndex] ?? string.Empty
                        : string.Empty;
                if (!IsMissingValue(raw))
                {
                    requests.Add((mapping.Namespace, raw));
                }
            }
        }

        IReadOnlyDictionary<(string, string), Pseudonym> resolved;
        using (phases.Measure(CsvJobPhase.ResolveDatabase))
        {
            resolved =
                requests.Count == 0
                    ? new Dictionary<(string, string), Pseudonym>()
                    : await pseudonymAppService.CreateTrustedBatchAsync(
                        requests,
                        CancellationToken.None
                    );
        }

        // Covers the whole write loop rather than each NextRecordAsync individually: the rows go
        // into a pipe CsvJobOutputUploader drains concurrently, so what is being measured here is
        // how long that pipe spends full - i.e. how much of this job is spent waiting on the
        // upload to keep up - and that only shows up in aggregate across a chunk.
        using var writeScope = phases.Measure(CsvJobPhase.WriteOutput);

        foreach (var row in chunk)
        {
            for (var i = 0; i < row.RawFields.Length; i++)
            {
                if (inPlaceBySourceIndex.TryGetValue(i, out var ns))
                {
                    var raw = row.RawFields[i] ?? string.Empty;
                    if (IsMissingValue(raw))
                    {
                        progress.MissingValueCount++;
                        csvWriter.WriteField(raw);
                    }
                    else
                    {
                        csvWriter.WriteField(resolved[(ns.Name, raw)].PseudonymValue);
                    }
                }
                else
                {
                    csvWriter.WriteField(row.RawFields[i] ?? string.Empty);
                }
            }

            foreach (var mapping in appended)
            {
                var raw =
                    mapping.SourceIndex < row.RawFields.Length
                        ? row.RawFields[mapping.SourceIndex] ?? string.Empty
                        : string.Empty;
                if (IsMissingValue(raw))
                {
                    progress.MissingValueCount++;
                    csvWriter.WriteField(raw);
                }
                else
                {
                    csvWriter.WriteField(resolved[(mapping.Namespace.Name, raw)].PseudonymValue);
                }
            }

            await csvWriter.NextRecordAsync();
        }
    }

    /// <summary>
    /// Depseudonymize path (unchanged from before batching was introduced): resolves every field
    /// of every row in <paramref name="chunk"/> concurrently (each call uses its own pooled
    /// DbContext under the hood - see PseudonymAppService's trusted methods), then writes the rows
    /// out in their original order once every result is ready. Order is preserved even though
    /// resolution completes out of order, since writing only starts after the whole chunk's
    /// <see cref="Task.WhenAll(Task[])"/> has completed.
    /// </summary>
    private async Task FlushChunkDepseudonymizeAsync(
        List<BufferedRow> chunk,
        PseudonymizationJobDirection direction,
        Dictionary<int, Namespace> inPlaceBySourceIndex,
        List<(int SourceIndex, string TargetColumn, Namespace Namespace)> appended,
        CsvWriter csvWriter,
        CsvJobProgressReporter progress,
        CsvJobPhaseTimer phases
    )
    {
        // Opened before the tasks are created, not just around the WhenAll: ResolveValueAsync runs
        // synchronously up to its first await, so some of the lookup work has already happened by
        // the time the last task is started.
        var resolveScope = phases.Measure(CsvJobPhase.ResolveDatabase);

        foreach (var row in chunk)
        {
            row.InPlaceResults = new Task<string>?[row.RawFields.Length];
            for (var i = 0; i < row.RawFields.Length; i++)
            {
                if (inPlaceBySourceIndex.TryGetValue(i, out var ns))
                {
                    row.InPlaceResults[i] = ResolveValueAsync(
                        direction,
                        ns,
                        row.RawFields[i] ?? string.Empty,
                        progress
                    );
                }
            }

            row.AppendedResults = new Task<string>[appended.Count];
            for (var a = 0; a < appended.Count; a++)
            {
                var mapping = appended[a];
                var raw =
                    mapping.SourceIndex < row.RawFields.Length
                        ? row.RawFields[mapping.SourceIndex] ?? string.Empty
                        : string.Empty;
                row.AppendedResults[a] = ResolveValueAsync(
                    direction,
                    mapping.Namespace,
                    raw,
                    progress
                );
            }
        }

        try
        {
            await Task.WhenAll(
                chunk.SelectMany(r =>
                    r.InPlaceResults.Where(t => t is not null)
                        .Cast<Task<string>>()
                        .Concat(r.AppendedResults)
                )
            );
        }
        finally
        {
            resolveScope.Dispose();
        }

        // Same reasoning as the pseudonymize path's write scope above.
        using var writeScope = phases.Measure(CsvJobPhase.WriteOutput);

        foreach (var row in chunk)
        {
            for (var i = 0; i < row.RawFields.Length; i++)
            {
                // Already completed - every task in this chunk was awaited via WhenAll above.
                csvWriter.WriteField(
                    row.InPlaceResults[i] is { } task
                        ? await task
                        : row.RawFields[i] ?? string.Empty
                );
            }

            foreach (var appendedTask in row.AppendedResults)
            {
                csvWriter.WriteField(await appendedTask);
            }

            await csvWriter.NextRecordAsync();
        }
    }

    private bool IsMissingValue(string raw) =>
        CsvJobFormat.IsMissingValue(raw, csvProcessingConfig.Value);

    private sealed class BufferedRow(string?[] rawFields)
    {
        public string?[] RawFields { get; } = rawFields;
        public Task<string>?[] InPlaceResults { get; set; } = [];
        public Task<string>[] AppendedResults { get; set; } = [];
    }

    /// <summary>
    /// Transforms one field according to the job's direction. Depseudonymize on a value with no
    /// matching pseudonym leaves it unchanged rather than failing the whole job or blanking it -
    /// the field is left exactly as it was found in the input, whether that's a genuine unknown
    /// pseudonym or a value that was never pseudonymized in the first place, so a partial/wrong
    /// column selection is inspectable in the output rather than silently destroying data. A
    /// blank/missing value (see <see cref="CsvJobFormat.IsMissingValue"/>) short-circuits before
    /// even attempting a lookup - same "leave it as-is" outcome, just skipping a DB round trip
    /// that could only ever miss.
    /// </summary>
    private async Task<string> ResolveValueAsync(
        PseudonymizationJobDirection direction,
        Namespace @namespace,
        string rawValue,
        CsvJobProgressReporter progress
    )
    {
        if (IsMissingValue(rawValue))
        {
            progress.MissingValueCount++;
            return rawValue;
        }

        if (direction == PseudonymizationJobDirection.Depseudonymize)
        {
            var pseudonym = await pseudonymAppService.ReverseLookupTrustedAsync(
                @namespace.Name,
                rawValue,
                CancellationToken.None
            );
            return pseudonym?.OriginalValue ?? rawValue;
        }

        var created = await pseudonymAppService.CreateTrustedAsync(
            @namespace,
            rawValue,
            CancellationToken.None
        );
        return created.PseudonymValue;
    }
}
