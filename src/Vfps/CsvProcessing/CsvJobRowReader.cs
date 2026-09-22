using CsvHelper;
using Hangfire;

namespace Vfps.CsvProcessing;

/// <summary>
/// Reads a job's input row by row, in chunks, and flushes each one - the loop shared by every
/// direction that transforms an input file (<see cref="CsvColumnTransformer"/> and
/// <see cref="CsvNamespaceImporter"/>; <see cref="CsvNamespaceExporter"/> has no input file to
/// read this way, its rows come from the database instead). Centralized here rather than
/// duplicated, because every piece of this loop's shape - what gets measured as
/// <see cref="CsvJobPhase.ParseInput"/>, which cancellation check runs per row versus per chunk
/// and why, when the trailing partial chunk still needs one, why a row count mismatch is fatal -
/// was independently tuned against a real, measured cost or a real correctness bug, and having it
/// in two copies risked the two silently drifting apart the next time either was touched.
/// </summary>
internal static class CsvJobRowReader
{
    /// <summary>
    /// Reads every row from <paramref name="csvReader"/>, buffering it into chunks of up to
    /// <paramref name="chunkSize"/> via <paramref name="createRow"/> and handing each full chunk -
    /// plus, at the end, the final partial one - to <paramref name="flushChunkAsync"/>. Returns
    /// the number of rows flushed, which on a natural end-of-input always equals the number read
    /// (see the invariant check below), and on a cooperative cancellation is however many made it
    /// out before the check-in that noticed.
    /// </summary>
    /// <param name="csvReader">The job's input, already past its header row if it has one.</param>
    /// <param name="chunkSize">Rows buffered before a flush - already clamped by the caller.</param>
    /// <param name="createRow">
    /// Builds one buffered row from its raw fields and its 1-based position among the input's
    /// data rows (header excluded). That position matters only to
    /// <see cref="CsvColumnTransformer"/>, which reports it in
    /// <see cref="UnresolvedOriginalValueException"/> - <see cref="CsvNamespaceImporter"/> simply
    /// ignores it.
    /// </param>
    /// <param name="flushChunkAsync">
    /// Resolves and writes out one chunk - the only part of this loop that differs per direction.
    /// </param>
    /// <param name="progress">Where a check-in reports progress to and learns of a cancellation.</param>
    /// <param name="phases">Where this read's own share of the job's wall clock goes.</param>
    /// <param name="cancellationToken">
    /// The job's Hangfire cancellation token - see the inline comments below for why its abort
    /// check and its <see cref="IJobCancellationToken.ShutdownToken"/> run at different cadences.
    /// </param>
    public static async Task<long> ReadInChunksAsync<TRow>(
        CsvReader csvReader,
        int chunkSize,
        Func<string?[], long, TRow> createRow,
        Func<List<TRow>, Task> flushChunkAsync,
        CsvJobProgressReporter progress,
        CsvJobPhaseTimer phases,
        IJobCancellationToken cancellationToken
    )
    {
        // rows: actually flushed so far - what's reported as progress and eventually returned.
        // totalRowsRead: consumed from the reader so far, flushed or not - this drives when
        // MaybeReportAndCheckCancelledAsync actually checks in, decoupled from the (potentially
        // much larger, for a pseudonymize job) flush boundary so a big batch size can't blunt
        // cancellation/progress responsiveness. If a cancellation is noticed while a chunk is
        // only partially buffered, that partial buffer is simply dropped rather than flushed -
        // same "discard whatever hasn't been written yet" behavior as always, just checked more
        // often than the flush boundary alone would allow for.
        var rows = 0L;
        var totalRowsRead = 0L;
        var chunk = new List<TRow>(chunkSize);

        // A `while (true)` rather than `while (await csvReader.ReadAsync())` purely so the read
        // can be timed: parsing is where this job waits on the input object's bytes, and lumping
        // it in with the work done per row afterwards would hide whether a slow job is slow
        // because S3 is not delivering.
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

                // ShutdownToken, not the Hangfire token itself: IJobCancellationToken's own
                // ThrowIfCancellationRequested() takes a lock and issues a `GetStateData` query
                // against Hangfire's storage on every call (Hangfire 1.8's
                // ServerJobCancellationToken), so calling it per row costs one database round trip
                // per row - which measured as ~84% of a 1M-row job's wall clock, billed to
                // ParseInput because it sits inside this scope. The abort check it performs is
                // made once per chunk instead (see the flush below), and a user-initiated cancel
                // is already noticed by CsvJobProgressReporter.MaybeReportAndCheckCancelledAsync.
                // ShutdownToken is a plain CancellationToken, so this stays a free flag read.
                cancellationToken.ShutdownToken.ThrowIfCancellationRequested();

                var fieldCount = csvReader.Parser.Count;
                rawFields = new string?[fieldCount];
                for (var i = 0; i < fieldCount; i++)
                {
                    rawFields[i] = csvReader.GetField(i);
                }
            }

            totalRowsRead++;
            chunk.Add(createRow(rawFields, totalRowsRead));

            if (chunk.Count >= chunkSize)
            {
                // The Hangfire abort check, at chunk granularity rather than per row - see the
                // note on ShutdownToken above for why it cannot go in the row loop.
                cancellationToken.ThrowIfCancellationRequested();
                await flushChunkAsync(chunk);
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
            // Also here, not just at the full-chunk flushes above: a file shorter than one chunk
            // would otherwise never reach a Hangfire abort check at all, and a job whose fetch has
            // been reassigned to another worker must not go on to write output.
            cancellationToken.ThrowIfCancellationRequested();
            await flushChunkAsync(chunk);
            rows += chunk.Count;
        }

        // Every row consumed from the input must end up written to the output - a job whose
        // output has a different row count than its input would mean rows were silently dropped
        // or duplicated somewhere in the chunking/flush logic above. That's a correctness bug
        // serious enough to fail the job over rather than complete it and let a caller unknowingly
        // rely on a truncated/corrupted file. Only reached on a natural end-of-input - the early
        // `return rows` above on cancellation is intentionally exempt, since fewer output rows
        // than input rows is expected there.
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
}
