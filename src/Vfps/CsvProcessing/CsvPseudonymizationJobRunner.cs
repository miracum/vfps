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
    IAmazonS3 s3,
    IOptions<S3Config> s3Config,
    ILogger<CsvPseudonymizationJobRunner> logger
) : ICsvPseudonymizationJobRunner
{
    private const int ProgressUpdateRowInterval = 200;
    private static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromSeconds(2);

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

            var inPlaceBySourceIndex = new Dictionary<int, string>();
            var appended = new List<(int SourceIndex, string TargetColumn, string Namespace)>();
            foreach (var mapping in job.ColumnMappings)
            {
                var sourceIndex = ResolveColumnIndex(mapping.SourceColumn, header);
                if (mapping.TargetColumn is null)
                {
                    inPlaceBySourceIndex.TryAdd(sourceIndex, mapping.Namespace);
                }
                else
                {
                    appended.Add((sourceIndex, mapping.TargetColumn, mapping.Namespace));
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
            while (await csvReader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fieldCount = csvReader.Parser.Count;
                var rawFields = new string?[fieldCount];
                for (var i = 0; i < fieldCount; i++)
                {
                    rawFields[i] = csvReader.GetField(i);
                }

                for (var i = 0; i < fieldCount; i++)
                {
                    var raw = rawFields[i] ?? string.Empty;
                    if (inPlaceBySourceIndex.TryGetValue(i, out var ns))
                    {
                        var pseudonym = await pseudonymAppService.CreateTrustedAsync(
                            ns,
                            raw,
                            CancellationToken.None
                        );
                        csvWriter.WriteField(pseudonym.PseudonymValue);
                    }
                    else
                    {
                        csvWriter.WriteField(raw);
                    }
                }

                foreach (var a in appended)
                {
                    var raw =
                        a.SourceIndex < rawFields.Length
                            ? rawFields[a.SourceIndex] ?? string.Empty
                            : string.Empty;
                    var pseudonym = await pseudonymAppService.CreateTrustedAsync(
                        a.Namespace,
                        raw,
                        CancellationToken.None
                    );
                    csvWriter.WriteField(pseudonym.PseudonymValue);
                }

                await csvWriter.NextRecordAsync();
                rows++;

                if (
                    rows % ProgressUpdateRowInterval == 0
                    || sinceLastUpdate.Elapsed >= ProgressUpdateInterval
                )
                {
                    await jobRepository.UpdateProgressAsync(
                        job.Id,
                        countingStream.BytesRead,
                        rows,
                        CancellationToken.None
                    );
                    sinceLastUpdate.Restart();

                    var current = await jobRepository.FindAsync(job.Id, CancellationToken.None);
                    if (current?.Status == PseudonymizationJobStatus.Cancelled)
                    {
                        return rows;
                    }
                }
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
