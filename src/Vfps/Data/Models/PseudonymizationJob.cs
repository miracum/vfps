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

    /// <summary>
    /// Set only by <see cref="CsvProcessing.StalledPseudonymizationJobWatchdogService"/>: a
    /// Running job with no progress update in over its configured threshold, most often because
    /// its worker crashed, lost its database connection, or was killed mid-processing (e.g. by an
    /// app restart/upgrade) - never set by <see cref="CsvProcessing.CsvPseudonymizationJobRunner"/>
    /// itself. Kept distinct from <see cref="Failed"/> (a run that itself hit an exception) since
    /// this is a heuristic guess rather than a confirmed failure - occasionally the job was still
    /// alive and actually finishes shortly after being marked Stalled. Appended at the end, not
    /// inserted, so existing stored integer values keep their meaning.
    /// </summary>
    Stalled,
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

    /// <summary>
    /// The uploaded file's original browser-side name (e.g. "patients_2026.csv") - display only,
    /// so the jobs list is recognizable at a glance instead of showing only <see cref="Id"/>.
    /// Never used to derive <see cref="InputObjectKey"/> or any other path - that's always the
    /// deterministic "csv-jobs/{Id}/input.csv" pattern, regardless of what this is. Null for jobs
    /// created before this field existed, or if the browser-side lookup failed for any reason -
    /// treat as optional everywhere.
    /// </summary>
    public string? OriginalFileName { get; set; }

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
