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
/// </summary>
public class CsvPseudonymizationJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public CsvJobStatus Status { get; set; } = CsvJobStatus.Queued;
    public required string InputFilePath { get; init; }
    public string? OutputFilePath { get; set; }
    public required string[] ColumnsToProcess { get; init; }
    public required string NamespaceName { get; init; }
    public long RowsProcessed { get; set; }
    public long? TotalRows { get; set; }
    public string? ErrorMessage { get; set; }
}
