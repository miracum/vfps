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

        // The mapping's own SourceColumn string is carried along rather than just the index it
        // resolves to: it is what the caller typed (a header name, or an index), and it is what a
        // PseudonymizeMode.FailIfMissing job names when it reports where it stopped.
        var inPlaceBySourceIndex = new Dictionary<int, MappedColumn>();
        var appended = new List<AppendedColumn>();
        foreach (var mapping in job.ColumnMappings)
        {
            var sourceIndex = CsvJobFormat.ResolveColumnIndex(mapping.SourceColumn, header);
            var column = new MappedColumn(namespaces[mapping.Namespace], mapping.SourceColumn);
            if (mapping.TargetColumn is null)
            {
                inPlaceBySourceIndex.TryAdd(sourceIndex, column);
            }
            else
            {
                appended.Add(new AppendedColumn(sourceIndex, mapping.TargetColumn, column));
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

        // Both directions now resolve a chunk in one batched round trip per namespace, so both
        // take the same size - a de-pseudonymizing job no longer holds one pooled connection per
        // row and no longer needs its own, much smaller, pool-protecting bound.
        // Clamped rather than trusted as-is: a misconfigured 0 or negative value would otherwise
        // throw out of the List<BufferedRow> capacity below or flush on every single row.
        var chunkSize = Math.Max(1, csvProcessingConfig.Value.PseudonymizeBatchSize);
        var phases = context.Phases;

        // The read/chunk/flush/cancellation/progress loop itself is shared with
        // CsvNamespaceImporter - see CsvJobRowReader for why, and for everything that's
        // deliberate about its shape.
        return await CsvJobRowReader.ReadInChunksAsync(
            csvReader,
            chunkSize,
            (rawFields, rowNumber) => new BufferedRow(rawFields, rowNumber),
            chunk =>
                FlushChunkAsync(
                    chunk,
                    job,
                    inPlaceBySourceIndex,
                    appended,
                    csvWriter,
                    progress,
                    phases
                ),
            progress,
            phases,
            cancellationToken
        );
    }

    /// <summary>
    /// Writes out every row in <paramref name="chunk"/>, resolving each field first: collect the
    /// whole chunk's values, resolve them in one batched round trip per namespace, then write.
    ///
    /// One method for all three behaviors - pseudonymize, de-pseudonymize, and the two lookup-only
    /// <see cref="PseudonymizeMode"/>s - because they only ever differed in three things: which
    /// batch call resolves a chunk, which half of the resolved <see cref="Pseudonym"/> is written
    /// back, and what becomes of a value nothing resolved to. Separate methods are what would let
    /// the third of those drift.
    /// </summary>
    private async Task FlushChunkAsync(
        List<BufferedRow> chunk,
        PseudonymizationJob job,
        Dictionary<int, MappedColumn> inPlaceBySourceIndex,
        List<AppendedColumn> appended,
        CsvWriter csvWriter,
        CsvJobProgressReporter progress,
        CsvJobPhaseTimer phases
    )
    {
        var depseudonymize = job.Direction == PseudonymizationJobDirection.Depseudonymize;

        // Only a pseudonymizing job carries a mode - PseudonymizationJobAppService rejects one set
        // on any other direction, so this never has to second-guess what a Depseudonymize job's
        // PseudonymizeMode might have been left at.
        var lookupOnly =
            !depseudonymize && job.PseudonymizeMode != PseudonymizeMode.CreateIfMissing;

        // A blank cell (or a common "no value" placeholder - see CsvJobFormat.IsMissingValue) has
        // nothing to resolve: CreateTrustedBatchAsync rejects one outright, and either lookup
        // could only ever miss. Left out of the batch entirely, not merely skipped on write, so it
        // never occupies an upsert slot or a place in the resolved dictionary below.
        var requests = new List<(Namespace Namespace, string Value)>();
        foreach (var row in chunk)
        {
            foreach (var (sourceIndex, column) in inPlaceBySourceIndex)
            {
                var raw = row.RawFields[sourceIndex] ?? string.Empty;
                if (!IsMissingValue(raw))
                {
                    requests.Add((column.Namespace, raw));
                }
            }

            foreach (var mapping in appended)
            {
                var raw = ValueAt(row, mapping.SourceIndex);
                if (!IsMissingValue(raw))
                {
                    requests.Add((mapping.Column.Namespace, raw));
                }
            }
        }

        // The three resolvers share one shape - a dictionary keyed by (namespace, value) in which
        // an absent key means "nothing stored" - which is what lets the write loop below be
        // written once. Only CreateTrustedBatchAsync ever writes anything.
        IReadOnlyDictionary<(string, string), Pseudonym> resolved;
        using (phases.Measure(CsvJobPhase.ResolveDatabase))
        {
            if (requests.Count == 0)
            {
                resolved = new Dictionary<(string, string), Pseudonym>();
            }
            else if (depseudonymize)
            {
                resolved = await pseudonymAppService.ReverseLookupTrustedBatchAsync(
                    requests,
                    CancellationToken.None
                );
            }
            else if (lookupOnly)
            {
                resolved = await pseudonymAppService.ResolveTrustedBatchAsync(
                    requests,
                    CancellationToken.None
                );
            }
            else
            {
                resolved = await pseudonymAppService.CreateTrustedBatchAsync(
                    requests,
                    CancellationToken.None
                );
            }
        }

        // Checked across the whole chunk before a single field of it is written, so a job that
        // fails here emits no part of the chunk that failed, and the row it names is the first
        // unresolved one in file order rather than whichever the write loop happened to reach.
        if (!depseudonymize && job.PseudonymizeMode == PseudonymizeMode.FailIfMissing)
        {
            foreach (var row in chunk)
            {
                foreach (var (sourceIndex, column) in inPlaceBySourceIndex)
                {
                    EnsureResolved(row, column, row.RawFields[sourceIndex] ?? string.Empty);
                }

                foreach (var mapping in appended)
                {
                    EnsureResolved(row, mapping.Column, ValueAt(row, mapping.SourceIndex));
                }
            }
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
                if (inPlaceBySourceIndex.TryGetValue(i, out var column))
                {
                    csvWriter.WriteField(Resolve(column, row.RawFields[i] ?? string.Empty));
                }
                else
                {
                    csvWriter.WriteField(row.RawFields[i] ?? string.Empty);
                }
            }

            foreach (var mapping in appended)
            {
                csvWriter.WriteField(Resolve(mapping.Column, ValueAt(row, mapping.SourceIndex)));
            }

            await csvWriter.NextRecordAsync();
        }

        void EnsureResolved(BufferedRow row, MappedColumn column, string raw)
        {
            // A blank or placeholder input cell is not an unresolved value: there was nothing to
            // resolve in the first place. It is counted and passed through here exactly as in
            // every other mode, rather than failing a job over what a real-world CSV export
            // routinely contains.
            if (IsMissingValue(raw) || resolved.ContainsKey((column.Namespace.Name, raw)))
            {
                return;
            }

            throw new UnresolvedOriginalValueException(
                row.RowNumber,
                column.SourceLabel,
                column.Namespace.Name
            );
        }

        string Resolve(MappedColumn column, string raw)
        {
            if (IsMissingValue(raw))
            {
                progress.MissingValueCount++;
                return raw;
            }

            if (resolved.TryGetValue((column.Namespace.Name, raw), out var pseudonym))
            {
                return depseudonymize ? pseudonym.OriginalValue : pseudonym.PseudonymValue;
            }

            if (depseudonymize)
            {
                // Left exactly as found rather than blanked or failed over: whether this is a
                // genuinely unknown pseudonym or a value that was never pseudonymized at all, a
                // partial or wrong column selection stays inspectable in the output instead of
                // silently destroying data - and the field keeps a pseudonym either way.
                return raw;
            }

            if (job.PseudonymizeMode == PseudonymizeMode.KeepIfMissing)
            {
                // `raw`, unchanged - the counter is what tells the caller it happened.
                progress.UnresolvedValueCount++;
                return raw;
            }

            // Unreachable by design: CreateIfMissing gets an entry back for every value it asked
            // about, and FailIfMissing has already thrown above. Reaching here means a resolver
            // returned less than it was given, which must not be quietly written past.
            throw new InvalidOperationException(
                $"No pseudonym was resolved for a value in namespace '{column.Namespace.Name}', "
                    + $"processing a job in {job.PseudonymizeMode} mode."
            );
        }
    }

    /// <summary>
    /// A row shorter than the mapped column's index reads as an empty field rather than throwing -
    /// appended mappings have always treated a ragged row that way.
    /// </summary>
    private static string ValueAt(BufferedRow row, int index) =>
        index < row.RawFields.Length ? row.RawFields[index] ?? string.Empty : string.Empty;

    private bool IsMissingValue(string raw) =>
        CsvJobFormat.IsMissingValue(raw, csvProcessingConfig.Value);

    /// <summary>
    /// One mapped source column: the namespace its values resolve in, plus the caller's own name
    /// for it - a header name or an index, exactly as typed into
    /// <see cref="ColumnMapping.SourceColumn"/> - carried solely so
    /// <see cref="UnresolvedOriginalValueException"/> can say which column stopped the job.
    /// </summary>
    private sealed record MappedColumn(Namespace Namespace, string SourceLabel);

    /// <summary>A mapping written into a new column rather than over its source.</summary>
    private sealed record AppendedColumn(int SourceIndex, string TargetColumn, MappedColumn Column);

    private sealed class BufferedRow(string?[] rawFields, long rowNumber)
    {
        public string?[] RawFields { get; } = rawFields;

        /// <summary>
        /// 1-based position among the input's data rows, header row excluded - what a
        /// <see cref="PseudonymizeMode.FailIfMissing"/> job reports as the place it stopped.
        /// </summary>
        public long RowNumber { get; } = rowNumber;
    }
}

/// <summary>
/// A <see cref="PseudonymizeMode.FailIfMissing"/> job met an original value its namespace holds no
/// pseudonym for.
///
/// Carries only where the job stopped - the data row, the source column and the namespace, all of
/// which the caller already knows from the job it submitted - and never the value itself: this
/// message is the one exception message in the CSV pipeline persisted verbatim to
/// <see cref="PseudonymizationJob.ErrorMessage"/> and shown in the admin UI, rather than being
/// replaced with the generic "see server logs" text that keeps row content out of the database.
/// </summary>
internal sealed class UnresolvedOriginalValueException(
    long dataRowNumber,
    string sourceColumn,
    string namespaceName
)
    : Exception(
        $"Stopped at data row {dataRowNumber}: namespace '{namespaceName}' holds no pseudonym for "
            + $"that row's value in column '{sourceColumn}', and this job is set to fail rather "
            + "than create one."
    )
{
    public long DataRowNumber { get; } = dataRowNumber;
    public string SourceColumn { get; } = sourceColumn;
    public string NamespaceName { get; } = namespaceName;
}
