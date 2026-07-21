using System.Diagnostics;
using System.Globalization;
using System.Text;
using Amazon.S3;
using Amazon.S3.Transfer;
using CsvHelper;
using CsvHelper.Configuration;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vfps.AppServices;
using Vfps.Config;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.CsvProcessing;

/// <inheritdoc cref="ICsvPseudonymizationJobRunner"/>
public class CsvPseudonymizationJobRunner(
    IPseudonymizationJobRepository jobRepository,
    IPseudonymAppService pseudonymAppService,
    INamespaceRepository namespaceRepository,
    IAmazonS3 s3,
    IOptions<S3Config> s3Config,
    ILogger<CsvPseudonymizationJobRunner> logger
) : ICsvPseudonymizationJobRunner
{
    private const int ProgressUpdateRowInterval = 200;
    private static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromSeconds(2);

    // Rows are buffered into chunks of this size, and every field resolution across a whole
    // chunk is kicked off concurrently (via Task.WhenAll) rather than awaited one row/field at a
    // time - the dominant per-row cost is database round-trip latency, and concurrent calls
    // overlap that latency instead of paying it sequentially. Kept well under the connection
    // pool's "Maximum Pool Size=50" default (see appsettings.Development.json/compose.yaml) so
    // one CSV job can't starve every other Hangfire worker or request of a connection.
    private const int ConcurrencyChunkSize = 20;

    /// <inheritdoc/>
    public async Task RunAsync(Guid jobId, IJobCancellationToken cancellationToken)
    {
        var job =
            await jobRepository.FindAsync(jobId, CancellationToken.None)
            ?? throw new InvalidOperationException(
                $"Pseudonymization job '{jobId}' does not exist."
            );

        // Guards against a manual re-run (e.g. via the Hangfire dashboard) of a job that's
        // already reached a terminal state - automatic retries are disabled (see Program.cs),
        // but nothing stops an operator from clicking "Retry" there directly.
        if (
            job.Status
            is PseudonymizationJobStatus.Cancelled
                or PseudonymizationJobStatus.Completed
                or PseudonymizationJobStatus.Failed
        )
        {
            return;
        }

        await jobRepository.UpdateStatusAsync(
            jobId,
            PseudonymizationJobStatus.Running,
            null,
            CancellationToken.None
        );

        try
        {
            var (outputObjectKey, rowsProcessed) = await ProcessAsync(job, cancellationToken);

            // A cooperative cancel may have landed while the last chunk was still uploading -
            // don't overwrite Cancelled with Completed.
            var current = await jobRepository.FindAsync(jobId, CancellationToken.None);
            if (current?.Status != PseudonymizationJobStatus.Cancelled)
            {
                await jobRepository.CompleteAsync(
                    jobId,
                    outputObjectKey,
                    rowsProcessed,
                    CancellationToken.None
                );
            }
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

    private async Task<(string OutputObjectKey, long RowsProcessed)> ProcessAsync(
        PseudonymizationJob job,
        IJobCancellationToken cancellationToken
    )
    {
        var outputObjectKey =
            $"{PseudonymizationJobAppService.S3ObjectKeyPrefix}{job.Id}/output.csv";
        var encoding = Encoding.GetEncoding(job.Encoding);
        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = job.Delimiter,
            HasHeaderRecord = job.HasHeaderRow,
            // Real-world exports sometimes contain a stray, unescaped '"' inside an otherwise
            // unquoted field (e.g. a free-text column with an inch mark). CsvHelper treats that as
            // malformed CSV and throws by default. Since fields here are opaque values to relocate
            // rather than data to interpret, keep the raw field content as-is and move on instead
            // of failing the whole job over one row. Never log the raw field/record - only the row
            // number - since that value is exactly what this service exists to protect.
            BadDataFound = args =>
                logger.LogWarning(
                    "Ignoring malformed CSV data on parser row {Row}",
                    args.Context.Parser?.Row
                ),
        };

        using var getResponse = await s3.GetObjectAsync(s3Config.Value.Bucket, job.InputObjectKey);
        var countingStream = new ByteCountingStream(getResponse.ResponseStream);

        var pipe = new System.IO.Pipelines.Pipe();

        var transformTask = TransformAsync(
            job,
            countingStream,
            encoding,
            csvConfig,
            pipe.Writer,
            cancellationToken
        );
        using var transferUtility = new TransferUtility(s3);
        var uploadTask = transferUtility.UploadAsync(
            pipe.Reader.AsStream(),
            s3Config.Value.Bucket,
            outputObjectKey,
            cancellationToken.ShutdownToken
        );

        await Task.WhenAll(transformTask, uploadTask);

        return (outputObjectKey, await transformTask);
    }

    /// <summary>
    /// Reads+transforms the input CSV row by row (via <c>GetField(index)</c> only - never a
    /// typed <c>GetField&lt;T&gt;()</c>, since fields here are opaque values to relocate, not
    /// data to interpret) and writes the result into <paramref name="pipeWriter"/>, which the
    /// caller concurrently uploads to S3 from the other end of the same <see cref="System.IO.Pipelines.Pipe"/>.
    /// </summary>
    private async Task<long> TransformAsync(
        PseudonymizationJob job,
        ByteCountingStream countingStream,
        Encoding encoding,
        CsvConfiguration csvConfig,
        System.IO.Pipelines.PipeWriter pipeWriter,
        IJobCancellationToken cancellationToken
    )
    {
        var pipeOutStream = pipeWriter.AsStream();
        try
        {
            using var reader = new StreamReader(countingStream, encoding);
            using var csvReader = new CsvReader(reader, csvConfig, leaveOpen: true);
            await using var writer = new StreamWriter(pipeOutStream, encoding, leaveOpen: true);
            await using var csvWriter = new CsvWriter(writer, csvConfig, leaveOpen: true);

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
                var sourceIndex = ResolveColumnIndex(mapping.SourceColumn, header);
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

            var rows = 0L;
            var sinceLastUpdate = Stopwatch.StartNew();
            var chunk = new List<BufferedRow>(ConcurrencyChunkSize);

            while (await csvReader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fieldCount = csvReader.Parser.Count;
                var rawFields = new string?[fieldCount];
                for (var i = 0; i < fieldCount; i++)
                {
                    rawFields[i] = csvReader.GetField(i);
                }

                chunk.Add(new BufferedRow(rawFields));

                if (chunk.Count < ConcurrencyChunkSize)
                {
                    continue;
                }

                await FlushChunkAsync(
                    chunk,
                    job.Direction,
                    inPlaceBySourceIndex,
                    appended,
                    csvWriter
                );
                rows += chunk.Count;
                chunk.Clear();

                if (
                    await MaybeReportProgressAndCheckCancelledAsync(
                        job.Id,
                        countingStream,
                        rows,
                        sinceLastUpdate
                    )
                )
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
                    csvWriter
                );
                rows += chunk.Count;
            }

            await jobRepository.UpdateProgressAsync(
                job.Id,
                countingStream.BytesRead,
                rows,
                CancellationToken.None
            );

            return rows;
        }
        finally
        {
            // Always complete the pipe, success or failure - otherwise the concurrent upload
            // task (reading from the other end of the same pipe) would hang forever.
            await pipeOutStream.DisposeAsync();
        }
    }

    /// <summary>
    /// Resolves every field of every row in <paramref name="chunk"/> concurrently (each call
    /// uses its own pooled DbContext under the hood - see PseudonymAppService's trusted methods),
    /// then writes the rows out in their original order once every result is ready. Order is
    /// preserved even though resolution completes out of order, since writing only starts after
    /// the whole chunk's <see cref="Task.WhenAll(Task[])"/> has completed.
    /// </summary>
    private async Task FlushChunkAsync(
        List<BufferedRow> chunk,
        PseudonymizationJobDirection direction,
        Dictionary<int, Namespace> inPlaceBySourceIndex,
        List<(int SourceIndex, string TargetColumn, Namespace Namespace)> appended,
        CsvWriter csvWriter
    )
    {
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
                        row.RawFields[i] ?? string.Empty
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
                row.AppendedResults[a] = ResolveValueAsync(direction, mapping.Namespace, raw);
            }
        }

        await Task.WhenAll(
            chunk.SelectMany(r =>
                r.InPlaceResults.Where(t => t is not null)
                    .Cast<Task<string>>()
                    .Concat(r.AppendedResults)
            )
        );

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

    /// <returns>true if the job was cancelled and processing should stop.</returns>
    private async Task<bool> MaybeReportProgressAndCheckCancelledAsync(
        Guid jobId,
        ByteCountingStream countingStream,
        long rows,
        Stopwatch sinceLastUpdate
    )
    {
        if (
            rows % ProgressUpdateRowInterval != 0
            && sinceLastUpdate.Elapsed < ProgressUpdateInterval
        )
        {
            return false;
        }

        await jobRepository.UpdateProgressAsync(
            jobId,
            countingStream.BytesRead,
            rows,
            CancellationToken.None
        );
        sinceLastUpdate.Restart();

        var current = await jobRepository.FindAsync(jobId, CancellationToken.None);
        return current?.Status == PseudonymizationJobStatus.Cancelled;
    }

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
    /// column selection is inspectable in the output rather than silently destroying data.
    /// </summary>
    private async Task<string> ResolveValueAsync(
        PseudonymizationJobDirection direction,
        Namespace @namespace,
        string rawValue
    )
    {
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

    private static int ResolveColumnIndex(string sourceColumn, string[]? header)
    {
        if (header is not null)
        {
            var index = Array.IndexOf(header, sourceColumn);
            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"Column '{sourceColumn}' was not found in the CSV header."
                );
            }

            return index;
        }

        if (!int.TryParse(sourceColumn, out var parsedIndex))
        {
            throw new InvalidOperationException(
                $"Column '{sourceColumn}' is not a valid 0-based column index (the file has no header row)."
            );
        }

        return parsedIndex;
    }
}
