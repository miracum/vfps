using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;

namespace Vfps.Data;

/// <inheritdoc/>
public class PseudonymRepository(IDbContextFactory<PseudonymContext> contextFactory)
    : IPseudonymRepository
{
    // The trailing ".seconds" (rather than relying on the exporter's unit-based suffixing) keeps
    // the exported Prometheus name identical to what this repository exposed under
    // prometheus-net: "vfps_upsert_duration_seconds" / "vfps_batch_upsert_duration_seconds".
    private static readonly Histogram<double> UpsertDuration =
        Program.Meter.CreateHistogram<double>(
            "vfps.upsert.duration.seconds",
            unit: "s",
            description: "Histogram of the durations for upserting a pseudonym into the backend database."
        );

    private static readonly Histogram<double> BatchUpsertDuration =
        Program.Meter.CreateHistogram<double>(
            "vfps.batch.upsert.duration.seconds",
            unit: "s",
            description: "Histogram of the durations for upserting a batch of pseudonyms into the backend database in a single round trip."
        );

    // we can't yet use FlexLabs.Upsert and avoid manual SQL due to
    // support for returning the upserted entity missing: https://github.com/artiomchi/FlexLabs.Upsert/issues/29
    private readonly string PostgreSQLInsertCommand =
        @"
        WITH
            cte AS (
            INSERT INTO
                pseudonyms (namespace_name, original_value, pseudonym_value, sequence_number, created_at, last_updated_at)
            VALUES
                ({0}, {1}, {2}, {3}, NOW(), NOW()) ON CONFLICT (namespace_name, original_value, sequence_number)
            DO NOTHING RETURNING *
            )
        SELECT *
        FROM cte
        UNION
        SELECT *
        FROM pseudonyms
        WHERE namespace_name={0} AND original_value={1} AND sequence_number={3}
    ";

    private readonly string SqliteInsertCommand =
        @"
        INSERT INTO pseudonyms (namespace_name, original_value, pseudonym_value, sequence_number, created_at, last_updated_at)
        VALUES ({0}, {1}, {2}, {3}, time('now'), time('now'))
        ON CONFLICT (namespace_name, original_value, sequence_number)
        DO UPDATE SET original_value=excluded.original_value
        WHERE original_value IS excluded.original_value
        RETURNING *;
    ";

    private async Task<Pseudonym?> UpsertAsync(PseudonymContext context, Pseudonym pseudonym)
    {
        var upsertCommand = context.Database.IsNpgsql()
            ? PostgreSQLInsertCommand
            : SqliteInsertCommand;

        Pseudonym? upsertedPseudonym = null;
        var retryCount = 3;
        while (upsertedPseudonym is null && retryCount > 0)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var pseudonyms = await context
                    .Pseudonyms.FromSqlRaw(
                        upsertCommand,
                        pseudonym.NamespaceName,
                        pseudonym.OriginalValue,
                        pseudonym.PseudonymValue,
                        pseudonym.SequenceNumber
                    )
                    .AsNoTracking()
                    .ToListAsync();
                upsertedPseudonym = pseudonyms.FirstOrDefault();
            }
            finally
            {
                UpsertDuration.Record(stopwatch.Elapsed.TotalSeconds);
            }

            retryCount--;
        }

        return upsertedPseudonym;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Pseudonym>> CreateIfNotExistBatchAsync(
        IReadOnlyList<Pseudonym> pseudonyms,
        CancellationToken cancellationToken
    )
    {
        if (pseudonyms.Count == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        List<Pseudonym> upserted;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (context.Database.IsNpgsql())
            {
                var sql = BuildBatchUpsertSql(pseudonyms, out var parameters);
                upserted = await context
                    .Pseudonyms.FromSqlRaw(sql, parameters)
                    .AsNoTracking()
                    .ToListAsync(cancellationToken);
            }
            else
            {
                // SQLite is test-only (never production scale - see ListByNamespaceAsync above),
                // so a plain loop over the already-proven single-row upsert is simpler than
                // maintaining a second batched SQL dialect purely for test scaffolding.
                upserted = await SequentialFallbackAsync(context, pseudonyms);
            }
        }
        finally
        {
            BatchUpsertDuration.Record(stopwatch.Elapsed.TotalSeconds);
        }

        // The batched round trip is expected to cover every requested key. It can fall short only
        // if a concurrent writer (a different Hangfire job, or another connection entirely)
        // inserts the exact same key between this statement's INSERT and its own fallback SELECT
        // - the same rare race UpsertAsync's retry loop above exists to handle. Reuse that
        // proven, single-row retry logic here instead of duplicating it for the batch case.
        if (upserted.Count < pseudonyms.Count)
        {
            var covered = upserted
                .Select(p => (p.NamespaceName, p.OriginalValue, p.SequenceNumber))
                .ToHashSet();
            foreach (
                var missing in pseudonyms.Where(p =>
                    !covered.Contains((p.NamespaceName, p.OriginalValue, p.SequenceNumber))
                )
            )
            {
                var single = await UpsertAsync(context, missing);
                if (single is not null)
                {
                    upserted.Add(single);
                }
            }
        }

        return upserted;
    }

    private async Task<List<Pseudonym>> SequentialFallbackAsync(
        PseudonymContext context,
        IReadOnlyList<Pseudonym> pseudonyms
    )
    {
        var results = new List<Pseudonym>(pseudonyms.Count);
        foreach (var pseudonym in pseudonyms)
        {
            var single = await UpsertAsync(context, pseudonym);
            if (single is not null)
            {
                results.Add(single);
            }
        }

        return results;
    }

    /// <summary>
    /// Builds one combined "insert whatever's missing, then return every requested row" query
    /// for the whole batch - the same shape as <see cref="PostgreSQLInsertCommand"/>, generalized
    /// from one row to N. A single multi-row INSERT ... ON CONFLICT DO NOTHING tolerates the same
    /// key appearing more than once in <paramref name="pseudonyms"/> (Postgres resolves the
    /// conflict against rows already inserted earlier in the same statement), and the closing
    /// UNION (not UNION ALL) dedupes the resulting rows - so this is correct even without the
    /// caller enforcing the "already deduped" precondition, it just costs an extra row on the wire.
    /// </summary>
    // Internal, not private: this is the one piece of CreateIfNotExistBatchAsync unit tests can
    // never otherwise exercise, since ServiceTestBase always runs against SQLite (see
    // CreateIfNotExistBatchAsync's own comment on why) - only ever hit for real against Postgres
    // in production. Exposed (via InternalsVisibleTo) so PseudonymRepositoryTests can verify the
    // generated SQL/parameter shape directly, as a pure function, without needing a real Postgres
    // connection.
    internal static string BuildBatchUpsertSql(
        IReadOnlyList<Pseudonym> pseudonyms,
        out object[] parameters
    )
    {
        // The join on sequence_number too (not just namespace_name/original_value) matters for a
        // multi-psn namespace (Namespace.AllowsMultiplePseudonyms): several rows can share
        // (namespace_name, original_value) there, and without it this would return every one of
        // them for a single input row instead of exactly the one sequence number that row asked
        // for.
        return $"""
            {BuildUpsertCte(pseudonyms, out parameters)}
            SELECT *
            FROM inserted
            UNION
            SELECT p.*
            FROM pseudonyms p
            JOIN input i ON p.namespace_name = i.namespace_name
                AND p.original_value = i.original_value
                AND p.sequence_number = i.sequence_number
            """;
    }

    /// <summary>
    /// The part <see cref="BuildBatchUpsertSql"/> and <see cref="BuildSetUpsertSql"/> share: an
    /// <c>input</c> CTE with one row per pseudonym, and an <c>inserted</c> CTE that stores
    /// whichever of them aren't already there - narrowed further by <paramref name="inputFilter"/>,
    /// a WHERE clause over the <c>input</c> rows, if given. Callers append the final SELECT.
    /// </summary>
    private static string BuildUpsertCte(
        IReadOnlyList<Pseudonym> pseudonyms,
        out object[] parameters,
        string inputFilter = ""
    )
    {
        var values = new StringBuilder();
        parameters = new object[pseudonyms.Count * 4];
        for (var i = 0; i < pseudonyms.Count; i++)
        {
            if (i > 0)
            {
                values.Append(", ");
            }

            var baseIndex = i * 4;
            values
                .Append('(')
                .Append('{')
                .Append(baseIndex)
                .Append("}, {")
                .Append(baseIndex + 1)
                .Append("}, {")
                .Append(baseIndex + 2)
                .Append("}, {")
                .Append(baseIndex + 3)
                .Append("})");

            parameters[baseIndex] = pseudonyms[i].NamespaceName;
            parameters[baseIndex + 1] = pseudonyms[i].OriginalValue;
            parameters[baseIndex + 2] = pseudonyms[i].PseudonymValue;
            parameters[baseIndex + 3] = pseudonyms[i].SequenceNumber;
        }

        return $"""
            WITH
                input (namespace_name, original_value, pseudonym_value, sequence_number) AS (
                    VALUES {values}
                ),
                inserted AS (
                    INSERT INTO
                        pseudonyms (namespace_name, original_value, pseudonym_value, sequence_number, created_at, last_updated_at)
                    SELECT namespace_name, original_value, pseudonym_value, sequence_number, NOW(), NOW()
                    FROM input
                    {inputFilter}
                    ON CONFLICT (namespace_name, original_value, sequence_number) DO NOTHING
                    RETURNING *
                )
            """;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Pseudonym>> ListByNamespaceAsync(
        string namespaceName,
        PseudonymPageCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (context.Database.IsNpgsql())
        {
            // Raw SQL, matching the existing precedent in UpsertAsync above: a row-value
            // comparison maps directly onto the (namespace_name, created_at, original_value)
            // index as a range scan, which a LINQ-translated equivalent isn't guaranteed to do.
            if (cursor is null)
            {
                return await context
                    .Pseudonyms.FromSqlInterpolated(
                        $"""
                        SELECT * FROM pseudonyms
                        WHERE namespace_name = {namespaceName}
                        ORDER BY created_at DESC, original_value DESC, sequence_number DESC
                        LIMIT {pageSize}
                        """
                    )
                    .AsNoTracking()
                    .ToListAsync(cancellationToken);
            }

            return await context
                .Pseudonyms.FromSqlInterpolated(
                    $"""
                    SELECT * FROM pseudonyms
                    WHERE namespace_name = {namespaceName}
                      AND (created_at, original_value, sequence_number)
                        < ({cursor.CreatedAt}, {cursor.OriginalValue}, {cursor.SequenceNumber})
                    ORDER BY created_at DESC, original_value DESC, sequence_number DESC
                    LIMIT {pageSize}
                    """
                )
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }

        // SQLite (unit tests only, never production scale): page in memory to sidestep any
        // uncertainty about how this provider translates a composite keyset comparison.
        var all = await context
            .Pseudonyms.AsNoTracking()
            .Where(p => p.NamespaceName == namespaceName)
            .ToListAsync(cancellationToken);

        IEnumerable<Pseudonym> ordered = all.OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.OriginalValue, StringComparer.Ordinal)
            .ThenByDescending(p => p.SequenceNumber);

        if (cursor is not null)
        {
            ordered = ordered.SkipWhile(p =>
                !(
                    p.CreatedAt < cursor.CreatedAt
                    || (
                        p.CreatedAt == cursor.CreatedAt
                        && string.CompareOrdinal(p.OriginalValue, cursor.OriginalValue) < 0
                    )
                    || (
                        p.CreatedAt == cursor.CreatedAt
                        && p.OriginalValue == cursor.OriginalValue
                        && p.SequenceNumber < cursor.SequenceNumber
                    )
                )
            );
        }

        return [.. ordered.Take(pageSize)];
    }

    /// <inheritdoc/>
    public async Task<long> CountByNamespaceAsync(
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .Pseudonyms.AsNoTracking()
            .Where(p => p.NamespaceName == namespaceName)
            .LongCountAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, long>> CountAllGroupedByNamespaceAsync(
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var counted = await context
            .Pseudonyms.AsNoTracking()
            .GroupBy(p => p.NamespaceName)
            .Select(g => new { Namespace = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Namespace, x => x.Count, cancellationToken);

        // A GROUP BY over pseudonyms can't produce a row for a namespace that has none, so the
        // namespaces are listed separately and the gaps filled with zero. Deliberately a second
        // query rather than the obvious LEFT JOIN from namespaces: the join forces every row
        // through a hash join before aggregating, which costs the parallel partial aggregate that
        // makes the count above bearable at all (measured on 5M rows: 346ms for these two queries
        // against 1493ms for the join). This one reads a table with a row per namespace.
        var allNamespaces = await context
            .Namespaces.AsNoTracking()
            .Select(n => n.Name)
            .ToListAsync(cancellationToken);

        return allNamespaces.ToDictionary(
            name => name,
            name => counted.GetValueOrDefault(name, 0L)
        );
    }

    /// <inheritdoc/>
    public async Task<(IReadOnlyList<Pseudonym> Items, long TotalCount)> SearchByNamespaceAsync(
        string namespaceName,
        string? searchText,
        bool includeOriginalValueInSearch,
        int skip,
        int take,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Pseudonyms.AsNoTracking().Where(p => p.NamespaceName == namespaceName);

        var trimmedSearch = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
        if (trimmedSearch is not null)
        {
            if (context.Database.IsNpgsql())
            {
                var pattern = $"%{EscapeLikePattern(trimmedSearch)}%";
                query = includeOriginalValueInSearch
                    ? query.Where(p =>
                        EF.Functions.ILike(p.PseudonymValue, pattern, LikeEscapeCharacter)
                        || EF.Functions.ILike(p.OriginalValue, pattern, LikeEscapeCharacter)
                    )
                    : query.Where(p =>
                        EF.Functions.ILike(p.PseudonymValue, pattern, LikeEscapeCharacter)
                    );
            }
            else
            {
                // SQLite (test-only, never production scale - same reasoning as
                // ListByNamespaceAsync above): EF.Functions.ILike is Npgsql-only, so fall back to
                // a plain case-insensitive Contains, which EF translates to SQLite's LOWER().
                var needle = trimmedSearch.ToLowerInvariant();
                query = includeOriginalValueInSearch
                    ? query.Where(p =>
                        p.PseudonymValue.ToLower().Contains(needle)
                        || p.OriginalValue.ToLower().Contains(needle)
                    )
                    : query.Where(p => p.PseudonymValue.ToLower().Contains(needle));
            }
        }

        var totalCount = await query.LongCountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.PseudonymValue)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    private const string LikeEscapeCharacter = "\\";

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <inheritdoc/>
    public async Task<Pseudonym?> FindByPseudonymValueAsync(
        string namespaceName,
        string pseudonymValue,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .Pseudonyms.AsNoTracking()
            .Where(p => p.NamespaceName == namespaceName && p.PseudonymValue == pseudonymValue)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Pseudonym>> FindAllByPseudonymValuesAsync(
        string namespaceName,
        IReadOnlyCollection<string> pseudonymValues,
        CancellationToken cancellationToken
    )
    {
        if (pseudonymValues.Count == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Contains() over a collection is translated by the Npgsql provider to `= ANY(@p)` - one
        // array parameter, so the SQL text is identical whatever the chunk size and stays
        // plan-cacheable, rather than an IN list whose parameter count changes per call.
        return await context
            .Pseudonyms.AsNoTracking()
            .Where(p =>
                p.NamespaceName == namespaceName && pseudonymValues.Contains(p.PseudonymValue)
            )
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<string>> FilterExistingPseudonymValuesAsync(
        string namespaceName,
        IReadOnlyCollection<string> pseudonymValues,
        CancellationToken cancellationToken
    )
    {
        if (pseudonymValues.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var found = await context
            .Pseudonyms.AsNoTracking()
            .Where(p =>
                p.NamespaceName == namespaceName && pseudonymValues.Contains(p.PseudonymValue)
            )
            .Select(p => p.PseudonymValue)
            .Distinct()
            .ToListAsync(cancellationToken);

        return new HashSet<string>(found, StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Pseudonym>> FindAllByOriginalValueAsync(
        string namespaceName,
        string originalValue,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .Pseudonyms.AsNoTracking()
            .Where(p => p.NamespaceName == namespaceName && p.OriginalValue == originalValue)
            .OrderBy(p => p.SequenceNumber)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Pseudonym?> FindFirstByOriginalValueAsync(
        Namespace @namespace,
        string originalValue,
        CancellationToken cancellationToken
    )
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .Pseudonyms.AsNoTracking()
            .FirstOrDefaultAsync(
                p =>
                    p.NamespaceName == @namespace.Name
                    && p.OriginalValue == originalValue
                    && p.SequenceNumber == 0,
                cancellationToken
            );
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Pseudonym>> FindAllByOriginalValuesAsync(
        string namespaceName,
        IReadOnlyCollection<string> originalValues,
        CancellationToken cancellationToken
    )
    {
        if (originalValues.Count == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context
            .Pseudonyms.AsNoTracking()
            .Where(p =>
                p.NamespaceName == namespaceName && originalValues.Contains(p.OriginalValue)
            )
            .OrderBy(p => p.OriginalValue)
            .ThenBy(p => p.SequenceNumber)
            .ToListAsync(cancellationToken);
    }

    // Same bound as UpsertAsync's retry loop above, for the same rare race.
    private const int MaxSetUpsertAttempts = 3;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Pseudonym>> CreateSetIfNotExistAsync(
        Namespace @namespace,
        IReadOnlyList<Pseudonym> candidates,
        CancellationToken cancellationToken
    )
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var first = candidates[0];

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!context.Database.IsNpgsql())
        {
            // SQLite is test-only (see CreateIfNotExistBatchAsync above), so the extra round trips
            // of separate reads around the insert cost nothing that matters there. The read
            // before it stands in for BuildSetUpsertSql's NOT EXISTS.
            var storedValues = (
                await FindAllByOriginalValueAsync(
                    first.NamespaceName,
                    first.OriginalValue,
                    cancellationToken
                )
            )
                .Select(p => p.PseudonymValue)
                .ToHashSet(StringComparer.Ordinal);
            await CreateIfNotExistBatchAsync(
                [.. candidates.Where(c => !storedValues.Contains(c.PseudonymValue))],
                cancellationToken
            );
            return await FindAllByOriginalValueAsync(
                first.NamespaceName,
                first.OriginalValue,
                cancellationToken
            );
        }

        var sql = BuildSetUpsertSql(candidates, out var parameters);

        List<Pseudonym> stored = [];
        for (var attempt = 0; attempt < MaxSetUpsertAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                stored = await context
                    .Pseudonyms.FromSqlRaw(sql, parameters)
                    .AsNoTracking()
                    .ToListAsync(cancellationToken);
            }
            finally
            {
                BatchUpsertDuration.Record(stopwatch.Elapsed.TotalSeconds);
            }

            // A candidate goes missing for one of two reasons. A concurrent writer may have
            // committed the same key after this statement took its snapshot: the ON CONFLICT then
            // skips the candidate, but the snapshot can't see the winning row either. Re-running
            // the statement takes a fresh snapshot that does. Or its pseudonym value is already
            // stored for this original value, so the NOT EXISTS skipped it - that row is in
            // `stored`, and re-running would only skip it again, so the caller gets the set as it
            // is and fails the Create on the missing sequence number.
            var storedSequenceNumbers = stored.Select(p => p.SequenceNumber).ToHashSet();
            var storedValues = stored
                .Select(p => p.PseudonymValue)
                .ToHashSet(StringComparer.Ordinal);
            if (
                !candidates.Any(c =>
                    !storedSequenceNumbers.Contains(c.SequenceNumber)
                    && !storedValues.Contains(c.PseudonymValue)
                )
            )
            {
                break;
            }
        }

        return stored;
    }

    /// <summary>
    /// Builds the single round trip behind <see cref="CreateSetIfNotExistAsync"/>: insert
    /// whichever of the candidates' sequence numbers aren't stored yet, and return every row
    /// stored for their (namespace_name, original_value) - both the ones just inserted and the
    /// ones that were already there. The two halves of the UNION can't overlap: a data-modifying
    /// CTE's rows aren't visible to the rest of the statement's snapshot.
    /// </summary>
    /// <remarks>
    /// A candidate whose pseudonym value is already stored for the original value, under any
    /// sequence number, isn't inserted, so the value's pseudonyms stay distinct; its sequence
    /// number is simply missing from the result. The candidates themselves are expected to be
    /// distinct already. Two concurrent writers for the same value can't see each other's rows,
    /// so a collision between them isn't caught - only a unique index could, and the odds are
    /// the same as a collision with any other value in the namespace, which nothing prevents.
    /// </remarks>
    // Internal for the same reason as BuildBatchUpsertSql: Postgres-only, so unit tests can only
    // verify it as a pure function.
    internal static string BuildSetUpsertSql(
        IReadOnlyList<Pseudonym> candidates,
        out object[] parameters
    )
    {
        // Every candidate shares (namespace_name, original_value), so the first candidate's
        // placeholders ({0} and {1}) stand for all of them, in the NOT EXISTS as well as in the
        // final SELECT.
        const string skipValuesAlreadyStored = """
            WHERE NOT EXISTS (
                SELECT 1
                FROM pseudonyms p
                WHERE p.namespace_name = {0}
                    AND p.original_value = {1}
                    AND p.pseudonym_value = input.pseudonym_value
            )
            """;

        return $$"""
            {{BuildUpsertCte(candidates, out parameters, skipValuesAlreadyStored)}}
            SELECT *
            FROM inserted
            UNION
            SELECT *
            FROM pseudonyms
            WHERE namespace_name = {0} AND original_value = {1}
            ORDER BY sequence_number
            """;
    }
}
