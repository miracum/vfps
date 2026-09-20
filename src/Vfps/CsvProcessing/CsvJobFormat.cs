using System.Globalization;
using System.Text;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Vfps.Config;
using Vfps.Data.Models;

namespace Vfps.CsvProcessing;

/// <summary>
/// How a CSV job reads and writes cells: the CsvHelper configuration derived from the job's own
/// encoding/delimiter/header settings, and the rule deciding which cells hold no value at all.
/// Shared by every <see cref="ICsvJobProcessor"/> so a file parses identically no matter which
/// direction is processing it.
/// </summary>
internal static class CsvJobFormat
{
    /// <summary>
    /// Builds the reader/writer configuration for <paramref name="job"/>, with malformed rows
    /// counted on <paramref name="progress"/> instead of failing the job.
    /// </summary>
    public static CsvConfiguration CreateConfiguration(
        PseudonymizationJob job,
        CsvJobProgressReporter progress,
        ILogger logger
    ) =>
        new(CultureInfo.InvariantCulture)
        {
            Delimiter = job.Delimiter,
            HasHeaderRecord = job.HasHeaderRow,
            // Real-world exports sometimes contain a stray, unescaped '"' inside an otherwise
            // unquoted field (e.g. a free-text column with an inch mark). CsvHelper treats that as
            // malformed CSV and throws by default. Since fields here are opaque values to relocate
            // rather than data to interpret, keep the raw field content as-is and move on instead
            // of failing the whole job over one row. Never log the raw field/record - only the row
            // number - since that value is exactly what this service exists to protect. The count
            // is surfaced on the job itself so the UI can flag it.
            BadDataFound = args =>
            {
                progress.BadDataRowCount++;
                logger.LogWarning(
                    "Ignoring malformed CSV data on parser row {Row}",
                    args.Context.Parser?.Row
                );
            },
        };

    /// <summary>
    /// How much of the input object to pull per read of the underlying stream.
    ///
    /// <see cref="StreamReader"/>'s own default is 1 KiB, which for a CSV job means roughly a
    /// thousand reads - and a thousand awaits - per mebibyte of input, each one crossing the
    /// S3 response stream. Measured end to end against a local S3 (76 MiB, 2M rows), raising this
    /// cut the read phase from ~0.8s to ~0.5s; 16 KiB already captured effectively all of that
    /// and 256 KiB added nothing, so this sits just past the knee rather than as high as it
    /// could go.
    ///
    /// Treat that ~1.7x as a ceiling, not an expectation: it is what the overhead is worth when
    /// bytes are already local. A job reading from a genuinely remote bucket waits on bandwidth
    /// rather than on read syscalls, and gains correspondingly less. The cost is one buffer of
    /// this size (plus its decoded char buffer) per concurrently running job, so a few hundred
    /// KiB per <see cref="CsvProcessingConfig.WorkerCount"/>.
    /// </summary>
    private const int InputBufferSize = 64 * 1024;

    /// <summary>
    /// Opens the reader a job consumes its input object through. Shared by every direction that
    /// reads a file so they all get the same buffering, and so the byte-order-mark handling below
    /// can't drift apart between them.
    /// </summary>
    /// <param name="input">The job's input stream - in production a
    /// <see cref="ByteCountingStream"/> wrapping the S3 response.</param>
    /// <param name="encoding">The job's configured encoding.</param>
    public static StreamReader CreateReader(Stream input, Encoding encoding) =>
        new(
            input,
            encoding,
            // Kept explicitly true, which is what the no-buffer-size constructor this replaced
            // defaulted to. Turning it off would leave a UTF-8 BOM at the head of the file to be
            // parsed as part of the first header cell, so a column mapping naming that first
            // column would stop resolving - an obscure failure on exactly the files (Excel
            // exports) most likely to carry one.
            detectEncodingFromByteOrderMarks: true,
            InputBufferSize
        );

    /// <summary>
    /// Turns one <see cref="ColumnMapping"/> column reference into the field index to read it
    /// from: a header name when the file has a header row (<paramref name="header"/> non-null),
    /// or a 0-based column index when it doesn't. Throws rather than silently reading the wrong
    /// column - which for a pseudonymization job would mean writing the wrong data - so the job
    /// fails immediately, before a single row is processed.
    /// </summary>
    public static int ResolveColumnIndex(string sourceColumn, string[]? header)
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

    /// <summary>
    /// A truly blank/whitespace-only cell is always "no value here", regardless of configuration.
    /// <see cref="CsvProcessingConfig.MissingValuePlaceholders"/> (default "NA"/"NULL") adds named
    /// placeholder conventions real-world exports commonly use instead - matched
    /// case-insensitively and trimmed so stray surrounding whitespace doesn't defeat the match.
    /// Permissive by design: a dataset where a configured placeholder is coincidentally a real
    /// value would have it silently passed through untransformed, traded for not failing
    /// real-world exports outright - operators who hit that collision can narrow/clear the list.
    /// </summary>
    public static bool IsMissingValue(string raw, CsvProcessingConfig config)
    {
        var trimmed = raw.Trim();

        return trimmed.Length == 0
            || config.MissingValuePlaceholders.Any(placeholder =>
                trimmed.Equals(placeholder, StringComparison.OrdinalIgnoreCase)
            );
    }
}
