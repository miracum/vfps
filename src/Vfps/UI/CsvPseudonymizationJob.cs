namespace Vfps.UI;

/// <summary>
/// Possible states of a CSV pseudonymization job.
/// </summary>
public enum CsvJobStatus
{
    Queued,
    Processing,
    Done,
    Failed,
}

/// <summary>
/// Represents a CSV pseudonymization job.
/// The <see cref="InputKey"/> and <see cref="OutputKey"/> are storage keys whose meaning
/// depends on the active <see cref="ICsvFileStore"/> implementation:
/// for the local filesystem store they are absolute file paths;
/// for the S3 store they are S3 object keys.
/// </summary>
public class CsvPseudonymizationJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public CsvJobStatus Status { get; set; } = CsvJobStatus.Queued;

    /// <summary>Storage key for the uploaded input CSV.</summary>
    public required string InputKey { get; init; }

    /// <summary>Storage key for the pseudonymized output CSV, set once processing begins.</summary>
    public string? OutputKey { get; set; }

    public required string[] ColumnsToProcess { get; init; }
    public required string NamespaceName { get; init; }
    public long RowsProcessed { get; set; }
    public long? TotalRows { get; set; }
    public string? ErrorMessage { get; set; }
}
