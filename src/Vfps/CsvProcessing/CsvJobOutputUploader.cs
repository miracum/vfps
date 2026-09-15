using System.IO.Pipelines;
using System.Text;
using Amazon.S3;
using Amazon.S3.Transfer;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Options;
using Vfps.Config;

namespace Vfps.CsvProcessing;

/// <summary>
/// Streams a CSV job's output rows straight into object storage as they're produced, without ever
/// buffering the whole file in memory or on disk: the caller writes into a
/// <see cref="CsvWriter"/> on one end of a <see cref="Pipe"/> while
/// <see cref="TransferUtility"/> concurrently uploads from the other.
///
/// Shared by every direction that produces an output file - which is all of them (a
/// <see cref="Data.Models.PseudonymizationJobDirection.Import"/> job's output is its per-row
/// import report rather than a transformed copy of the input, but the plumbing is identical).
/// </summary>
internal sealed class CsvJobOutputUploader(IAmazonS3 s3, IOptions<S3Config> s3Config)
{
    /// <summary>
    /// Runs <paramref name="writeRows"/> against a <see cref="CsvWriter"/> whose bytes land in
    /// <paramref name="objectKey"/>, and returns whatever it returned (the rows written, for
    /// every current caller).
    /// </summary>
    /// <param name="objectKey">Object to upload the produced CSV to, in the configured bucket.</param>
    /// <param name="encoding">The job's configured output encoding.</param>
    /// <param name="csvConfig">The job's configured delimiter/header handling.</param>
    /// <param name="writeRows">Produces the output, one record at a time.</param>
    /// <param name="shutdownToken">
    /// Hangfire's server-shutdown token. Deliberately only wired to the upload half: the write
    /// half stops through the caller's own per-row cancellation check instead, which can unwind
    /// its database work cleanly rather than being torn out of the middle of a chunk.
    /// </param>
    public async Task<T> UploadAsync<T>(
        string objectKey,
        Encoding encoding,
        CsvConfiguration csvConfig,
        Func<CsvWriter, Task<T>> writeRows,
        CancellationToken shutdownToken
    )
    {
        var pipe = new Pipe();

        var writeTask = WriteAsync(pipe.Writer, encoding, csvConfig, writeRows);
        using var transferUtility = new TransferUtility(s3);
        var uploadTask = transferUtility.UploadAsync(
            pipe.Reader.AsStream(),
            s3Config.Value.Bucket,
            objectKey,
            shutdownToken
        );

        await Task.WhenAll(writeTask, uploadTask);

        return await writeTask;
    }

    private static async Task<T> WriteAsync<T>(
        PipeWriter pipeWriter,
        Encoding encoding,
        CsvConfiguration csvConfig,
        Func<CsvWriter, Task<T>> writeRows
    )
    {
        var pipeOutStream = pipeWriter.AsStream();
        try
        {
            await using var writer = new StreamWriter(pipeOutStream, encoding, leaveOpen: true);
            await using var csvWriter = new CsvWriter(writer, csvConfig, leaveOpen: true);

            return await writeRows(csvWriter);
        }
        finally
        {
            // Always complete the pipe, success or failure - otherwise the concurrent upload task
            // reading from the other end would hang forever, and with it the Task.WhenAll above,
            // turning any exception raised in here into a permanently stuck job rather than a
            // failed one.
            await pipeOutStream.DisposeAsync();
        }
    }
}
