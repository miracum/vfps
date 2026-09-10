namespace Vfps.Data.Models;

/// <summary>
/// How many pseudonyms one namespace held as of the last recompute - the shared, database-backed
/// result that lets every replica export the "vfps.pseudonyms" gauge while only one of them pays
/// for the count query. See <see cref="Metrics.PseudonymCountMetrics"/>.
///
/// One ordinary row per namespace, rather than a single row holding every series as JSON: the
/// counts are per-namespace and there is already a per-namespace table to hang them off, so a
/// relational shape needs no value converter, no comparer and no pre-seeded row, and an operator
/// can read it with a plain SELECT.
///
/// There is a row for every existing namespace, including ones holding no pseudonyms at all, which
/// store zero. A row therefore disappears only when its namespace does - which the foreign key below
/// takes care of - rather than whenever a namespace happens to be empty.
/// </summary>
public class PseudonymCount
{
    /// <summary>
    /// The namespace this count belongs to. Also the primary key - one row per namespace - and a
    /// foreign key to it, so a deleted namespace takes its count with it immediately rather than
    /// leaving a series behind until the next recompute notices.
    /// </summary>
    public string NamespaceName { get; set; } = "";

    /// <summary>The number of pseudonyms in the namespace at <see cref="ComputedAt"/>.</summary>
    public long Count { get; set; }

    /// <summary>
    /// When this row was last written. Purely informational - nothing schedules or coordinates off
    /// it (Hangfire owns the interval, see Program.cs) - but it's what tells an operator staring at
    /// a suspicious count whether it's current or the recompute has been failing.
    /// </summary>
    public DateTimeOffset ComputedAt { get; set; }
}
