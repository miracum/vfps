using System.ComponentModel.DataAnnotations;

namespace Vfps.Data.Models;

public enum PseudonymizationJobStatus
{
    /// <summary>Job record created, presigned upload URL issued, waiting for the input file.</summary>
    AwaitingUpload,

    /// <summary>Input file confirmed present in object storage, enqueued for processing.</summary>
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Which way a job's <see cref="ColumnMapping"/>s transform values. Determines both the required
/// permission at job creation (see
/// <see cref="AppServices.IPseudonymizationJobAppService.CreateJobAsync"/>) and which
/// <see cref="AppServices.IPseudonymAppService"/> method the runner calls per field.
/// </summary>
public enum PseudonymizationJobDirection
{
    /// <summary>Source column holds original values - replaced with their pseudonym. Requires write access.</summary>
    Pseudonymize,

    /// <summary>Source column holds pseudonym values - replaced with their original value. Requires reverse-lookup access.</summary>
    Depseudonymize,
}

/// <summary>
/// One column of a CSV job: replaces the value in <see cref="SourceColumn"/> - interpreted
/// according to the job's <see cref="PseudonymizationJob.Direction"/> - in
/// <see cref="Namespace"/>, either in place or into <see cref="TargetColumn"/>.
/// </summary>
public class ColumnMapping
{
    /// <summary>Header name (when the file has a header row) or a 0-based column index.</summary>
    public required string SourceColumn { get; set; }

    /// <summary>Null means replace <see cref="SourceColumn"/>'s value in place.</summary>
    public string? TargetColumn { get; set; }
    public required string Namespace { get; set; }
}

/// <summary>
/// A CSV pseudonymization (or de-pseudonymization - see <see cref="Direction"/>) job:
/// input/output files live in S3-compatible object storage (see <see cref="Config.S3Config"/>),
/// rows are processed by <see cref="CsvProcessing.CsvPseudonymizationJobRunner"/> via Hangfire.
/// No FK to <see cref="Namespace"/> - a single job's <see cref="ColumnMappings"/> can span
/// multiple namespaces.
/// </summary>
public class PseudonymizationJob : TracksCreationAndUpdates
{
    [Key]
    public Guid Id { get; set; }
    public PseudonymizationJobStatus Status { get; set; } =
        PseudonymizationJobStatus.AwaitingUpload;
    public PseudonymizationJobDirection Direction { get; set; } =
        PseudonymizationJobDirection.Pseudonymize;

    /// <summary>Subject ("sub" claim) of the user who created this job.</summary>
    public required string CreatedBy { get; set; }

    public required string InputObjectKey { get; set; }
    public string? OutputObjectKey { get; set; }

    public string Encoding { get; set; } = "utf-8";
    public string Delimiter { get; set; } = ",";
    public bool HasHeaderRow { get; set; } = true;
    public List<ColumnMapping> ColumnMappings { get; set; } = [];

    public long TotalBytes { get; set; }
    public long BytesProcessed { get; set; }
    public long RowsProcessed { get; set; }

    /// <summary>
    /// Rows CsvHelper flagged as malformed (e.g. a stray, unescaped quote inside an unquoted
    /// field) but recovered from rather than failing the job - see CsvPseudonymizationJobRunner's
    /// BadDataFound handler. The affected field's raw content is kept as-is; this is purely a
    /// count so the UI can flag "N rows had malformed data" without ever storing the data itself.
    /// </summary>
    public int BadDataRowCount { get; set; }

    /// <summary>
    /// Sanitized failure message only - never raw row content or a raw exception string, since
    /// this service's entire purpose is protecting the values that would otherwise leak here.
    /// </summary>
    public string? ErrorMessage { get; set; }
    public string? HangfireJobId { get; set; }
}
