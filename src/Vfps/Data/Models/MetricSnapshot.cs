namespace Vfps.Data.Models;

/// <summary>
/// The last computed value of a metric that's too expensive for every replica to compute on its
/// own, plus when it was computed.
///
/// One row per metric, holding the whole value set rather than a row per series: these snapshots
/// are only ever read and written as a complete set (never joined, filtered or aggregated in SQL),
/// so a single row is both the honest model of the access pattern and what makes "replace
/// wholesale" - the thing that lets a series which no longer exists actually disappear - a single
/// write rather than a diff.
///
/// </summary>
public class MetricSnapshot
{
    /// <summary>
    /// <see cref="Name"/> of the snapshot holding per-namespace pseudonym counts - the metric
    /// exported as "vfps.pseudonyms". Declared here rather than next to the metric so the model
    /// (and the HasData seed in <see cref="PseudonymContext"/>) doesn't have to reach into
    /// Vfps.Metrics.
    /// </summary>
    public const string PseudonymCountsName = "pseudonym-counts";

    /// <summary>
    /// Stable identifier for the metric this snapshot holds. Rows are seeded via HasData, so both
    /// a migrated database and a test's EnsureCreated get the row and nothing has to handle a
    /// missing one at runtime.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The metric's series, keyed by the value of its single dimension. Stored as JSON (jsonb on
    /// PostgreSQL) via a value converter, following the same pattern as
    /// <see cref="PseudonymizationJob.ColumnMappings"/>.
    /// </summary>
    public Dictionary<string, long> Values { get; set; } = [];

    /// <summary>
    /// When <see cref="Values"/> was last successfully written, stamped by the same statement that
    /// writes them. Not load-bearing - scheduling the recompute is Hangfire's job (see Program.cs),
    /// and nothing reads this to decide anything - but it's what tells an operator staring at a
    /// suspicious count whether the snapshot is current or the recompute has been failing.
    /// </summary>
    public DateTimeOffset ComputedAt { get; set; }
}
