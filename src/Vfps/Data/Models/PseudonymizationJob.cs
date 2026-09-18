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
/// What a CSV job does with the namespaces its <see cref="ColumnMapping"/>s reference. Determines
/// the required permission at job creation (see
/// <see cref="AppServices.IPseudonymizationJobAppService.CreateJobAsync"/>), and which processor
/// <see cref="CsvProcessing.CsvPseudonymizationJobRunner"/> hands the job to.
///
/// New members are appended, never inserted, so existing stored integer values keep their meaning.
/// </summary>
public enum PseudonymizationJobDirection
{
    /// <summary>Source column holds original values - replaced with their pseudonym. Requires write access.</summary>
    Pseudonymize,

    /// <summary>Source column holds pseudonym values - replaced with their original value. Requires reverse-lookup access.</summary>
    Depseudonymize,

    /// <summary>
    /// Bulk-loads already-known original/pseudonym pairs into a single namespace rather than
    /// generating pseudonyms - see <see cref="CsvProcessing.CsvNamespaceImporter"/>. The input's
    /// <see cref="ColumnMapping.SourceColumn"/> holds the original value and its
    /// <see cref="ColumnMapping.TargetColumn"/> the pseudonym to store for it; every other column
    /// is ignored. Requires write access, the same as <see cref="Pseudonymize"/>: it only ever
    /// stores pairs the caller already holds, and never reveals one it didn't.
    /// </summary>
    Import,

    /// <summary>
    /// Writes every pseudonym in a single namespace out as a two-column CSV - see
    /// <see cref="CsvProcessing.CsvNamespaceExporter"/>. The only direction with no input file at
    /// all (<see cref="PseudonymizationJob.InputObjectKey"/> is null). Requires reverse-lookup
    /// access, the same as <see cref="Depseudonymize"/> and for the same reason: the output is
    /// the namespace's original values, in bulk.
    /// </summary>
    Export,
}

/// <summary>
/// What a <see cref="PseudonymizationJobDirection.Pseudonymize"/> job does with an original value
/// that has no pseudonym in its namespace yet. Ignored by every other direction.
///
/// Deliberately one enum rather than a "look up only" flag plus a separate miss policy: the two
/// are not independent - there is no such thing as "create one, and also fail because there was
/// none" - and a single member per reachable behavior is what keeps that state unrepresentable
/// rather than merely unreachable.
///
/// Note what <see cref="BlankIfMissing"/> does *not* do: leave the field as it found it. That is
/// what a de-pseudonymizing job does with a pseudonym it cannot resolve (harmless - the value
/// stays a pseudonym), and doing the same here would write the raw original value into the very
/// column the job was asked to pseudonymize. The failure mode of this feature has to be an empty
/// field, never a leaked one.
///
/// New members are appended, never inserted, so existing stored integer values keep their meaning.
/// </summary>
public enum PseudonymizeMode
{
    /// <summary>
    /// Generate one. The default, and what every job did before this existed - so every row stored
    /// before this column existed reads back as this.
    /// </summary>
    CreateIfMissing,

    /// <summary>
    /// Never generate: fail the whole job at the first value with no pseudonym, before any part of
    /// the chunk it was found in is written. The job exposes no output file - like every other
    /// failure, whatever had already been streamed out is never linked as
    /// <see cref="PseudonymizationJob.OutputObjectKey"/> and expires with the bucket's lifecycle
    /// rule. For a caller whose file is supposed to contain only values the namespace already
    /// knows, where one that isn't means the input is wrong rather than that a pseudonym is wanted.
    /// </summary>
    FailIfMissing,

    /// <summary>
    /// Never generate: write an empty field for each value with no pseudonym, count them into
    /// <see cref="PseudonymizationJob.UnresolvedValueCount"/> and carry on. For a caller enriching
    /// a file where some rows simply have no counterpart yet.
    /// </summary>
    BlankIfMissing,
}

/// <summary>
/// One column of a CSV job: replaces the value in <see cref="SourceColumn"/> - interpreted
/// according to the job's <see cref="PseudonymizationJob.Direction"/> - in
/// <see cref="Namespace"/>, either in place or into <see cref="TargetColumn"/>.
///
/// <see cref="PseudonymizationJobDirection.Import"/> and
/// <see cref="PseudonymizationJobDirection.Export"/> jobs carry exactly one of these and read it
/// differently: <see cref="SourceColumn"/> names the original-value column and
/// <see cref="TargetColumn"/> the pseudonym-value column (both required there, rather than
/// TargetColumn being optional), with <see cref="Namespace"/> naming the single namespace being
/// imported into/exported from. Reusing this type is what lets those two directions inherit the
/// existing per-namespace permission checks and job plumbing unchanged, with no extra column on
/// <see cref="PseudonymizationJob"/> - see
/// <see cref="AppServices.PseudonymizationJobAppService.ResolveSingleNamespaceMapping"/>, which is
/// the only place that reads them back in that shape.
/// </summary>
public class ColumnMapping
{
    /// <summary>Header name (when the file has a header row) or a 0-based column index.</summary>
    public required string SourceColumn { get; set; }

    /// <summary>Null means replace <see cref="SourceColumn"/>'s value in place.</summary>
    public string? TargetColumn { get; set; }
    public required string Namespace { get; set; }

    /// <summary>
    /// <see cref="PseudonymizationJobDirection.Import"/> only: when set, each row's target
    /// namespace is read from this column of the row itself rather than being the single
    /// <see cref="Namespace"/> for the whole file - so one file can load pairs into many
    /// namespaces at once. <see cref="Namespace"/> is then unused and stored empty.
    ///
    /// Costs more to permit than it looks: the namespaces a file names are only known once it is
    /// being read, well after the caller who submitted it is gone, so there is no set to check
    /// access against at job creation. Write access to *every* namespace is therefore required
    /// instead - see <see cref="Authorization.NamespacePermissions.HasWriteAccessToAllNamespaces"/>.
    ///
    /// Null for every job created before this existed, and for every other direction - it is
    /// stored inside <see cref="PseudonymizationJob.ColumnMappings"/>'s JSON, so old rows simply
    /// deserialize without it.
    /// </summary>
    public string? NamespaceColumn { get; set; }
}

/// <summary>
/// A CSV job - pseudonymization, de-pseudonymization, namespace import or namespace export (see
/// <see cref="Direction"/>): input/output files live in S3-compatible object storage (see
/// <see cref="Config.S3Config"/>), rows are processed by
/// <see cref="CsvProcessing.CsvPseudonymizationJobRunner"/> via Hangfire. No FK to
/// <see cref="Namespace"/> - a single job's <see cref="ColumnMappings"/> can span multiple
/// namespaces (import/export are the exception: exactly one).
/// </summary>
public class PseudonymizationJob : TracksCreationAndUpdates
{
    [Key]
    public Guid Id { get; set; }
    public PseudonymizationJobStatus Status { get; set; } =
        PseudonymizationJobStatus.AwaitingUpload;
    public PseudonymizationJobDirection Direction { get; set; } =
        PseudonymizationJobDirection.Pseudonymize;

    /// <summary>
    /// Only meaningful when <see cref="Direction"/> is
    /// <see cref="PseudonymizationJobDirection.Pseudonymize"/> - rejected at job creation for any
    /// other direction rather than silently ignored, so a caller who sets it somewhere it cannot
    /// apply is told rather than quietly given the default behavior.
    /// </summary>
    public PseudonymizeMode PseudonymizeMode { get; set; } = PseudonymizeMode.CreateIfMissing;

    /// <summary>Subject ("sub" claim) of the user who created this job.</summary>
    public required string CreatedBy { get; set; }

    /// <summary>
    /// The object the runner reads this job's rows from. Null only for
    /// <see cref="PseudonymizationJobDirection.Export"/>, whose rows come from the database
    /// rather than from an uploaded file - every other direction always has one, assigned at
    /// creation time.
    /// </summary>
    public string? InputObjectKey { get; set; }
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
    /// Fields skipped rather than pseudonymized/de-pseudonymized because they were blank or a
    /// common "missing data" placeholder ("NA"/"NULL", case-insensitive - see
    /// CsvPseudonymizationJobRunner.IsMissingValue) - passed through to the output unchanged
    /// instead of failing the whole job over what a real-world export routinely contains. Purely
    /// a count so the UI can flag "N values were left as-is" without storing the values
    /// themselves.
    /// </summary>
    public int MissingValueCount { get; set; }

    /// <summary>
    /// <see cref="PseudonymizeMode.BlankIfMissing"/> only: fields written out empty because the
    /// namespace held no pseudonym for their original value and the job was told not to create
    /// one. Always 0 for every other mode - <see cref="PseudonymizeMode.CreateIfMissing"/> cannot
    /// leave a value unresolved, and <see cref="PseudonymizeMode.FailIfMissing"/> fails the job
    /// instead of counting.
    ///
    /// Distinct from <see cref="MissingValueCount"/>, which counts fields that were blank in the
    /// *input*: those two look identical in the output file and mean entirely different things -
    /// "there was nothing to pseudonymize" versus "there was, and this namespace has never seen
    /// it". Like the counters above, a count only: the values themselves are never stored.
    /// </summary>
    public int UnresolvedValueCount { get; set; }

    /// <summary>
    /// Sanitized failure message only - never raw row content or a raw exception string, since
    /// this service's entire purpose is protecting the values that would otherwise leak here.
    /// </summary>
    public string? ErrorMessage { get; set; }
    public string? HangfireJobId { get; set; }
}
