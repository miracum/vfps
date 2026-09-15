using System.Globalization;
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
