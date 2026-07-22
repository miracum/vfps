using System.Text;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using Vfps.Data.Models;

namespace Vfps.Data;

/// <inheritdoc/>
public class PseudonymRepository : IPseudonymRepository
{
    private static readonly Histogram UpsertDuration = Metrics.CreateHistogram(
        "vfps_upsert_duration_seconds",
        "Histogram of the durations for upserting a pseudonym into the backend database."
    );

    private static readonly Histogram BatchUpsertDuration = Metrics.CreateHistogram(
        "vfps_batch_upsert_duration_seconds",
        "Histogram of the durations for upserting a batch of pseudonyms into the backend database in a single round trip."
    );

    // we can't yet use FlexLabs.Upsert and avoid manual SQL due to
    // support for returning the upserted entity missing: https://github.com/artiomchi/FlexLabs.Upsert/issues/29
    private readonly string PostgreSQLInsertCommand =
        @"
        WITH
            cte AS (
            INSERT INTO
                pseudonyms (namespace_name, original_value, pseudonym_value, created_at, last_updated_at)
            VALUES
                ({0}, {1}, {2}, NOW(), NOW()) ON CONFLICT (namespace_name, original_value)
            DO NOTHING RETURNING *
            )
        SELECT *
        FROM cte
        UNION
        SELECT *
        FROM pseudonyms
        WHERE namespace_name={0} AND original_value={1}
    ";

    private readonly string SqliteInsertCommand =
        @"
        INSERT INTO pseudonyms (namespace_name, original_value, pseudonym_value, created_at, last_updated_at)
        VALUES ({0}, {1}, {2}, time('now'), time('now'))
        ON CONFLICT (namespace_name, original_value)
        DO UPDATE SET original_value=excluded.original_value
        WHERE original_value IS excluded.original_value
        RETURNING *;
    ";

    private readonly string UpsertCommand;

    /// <summary>
    /// Create a new instance of this pseudonym repository
    /// </summary>
    /// <param name="context">The database context</param>
    public PseudonymRepository(PseudonymContext context)
    {
        Context = context;

        if (Context.Database.IsNpgsql())
        {
            UpsertCommand = PostgreSQLInsertCommand;
        }
        else
        {
            UpsertCommand = SqliteInsertCommand;
        }
    }

    private PseudonymContext Context { get; }

    /// <inheritdoc/>
    public async Task<Pseudonym?> CreateIfNotExist(Pseudonym pseudonym)
    {
        Pseudonym? upsertedPseudonym = null;
        var retryCount = 3;
        while (upsertedPseudonym is null && retryCount > 0)
        {
            using (UpsertDuration.NewTimer())
            {
                var pseudonyms = await Context
                    .Pseudonyms.FromSqlRaw(
                        UpsertCommand,
                        pseudonym.NamespaceName,
                        pseudonym.OriginalValue,
                        pseudonym.PseudonymValue
                    )
                    .AsNoTracking()
                    .ToListAsync();
                upsertedPseudonym = pseudonyms.FirstOrDefault();
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

        List<Pseudonym> upserted;
        using (BatchUpsertDuration.NewTimer())
        {
            if (Context.Database.IsNpgsql())
            {
                var sql = BuildBatchUpsertSql(pseudonyms, out var parameters);
                upserted = await Context
                    .Pseudonyms.FromSqlRaw(sql, parameters)
                    .AsNoTracking()
                    .ToListAsync(cancellationToken);
            }
            else
            {
                // SQLite is test-only (never production scale - see ListByNamespaceAsync above),
                // so a plain loop over the already-proven single-row upsert is simpler than
                // maintaining a second batched SQL dialect purely for test scaffolding.
                upserted = await SequentialFallbackAsync(pseudonyms);
            }
        }

        // The batched round trip is expected to cover every requested key. It can fall short only
        // if a concurrent writer (a different Hangfire job, or another connection entirely)
        // inserts the exact same key between this statement's INSERT and its own fallback SELECT
        // - the same rare race CreateIfNotExist's retry loop above exists to handle. Reuse that
        // proven, single-row retry logic here instead of duplicating it for the batch case.
        if (upserted.Count < pseudonyms.Count)
        {
            var covered = upserted.Select(p => (p.NamespaceName, p.OriginalValue)).ToHashSet();
            foreach (
                var missing in pseudonyms.Where(p =>
                    !covered.Contains((p.NamespaceName, p.OriginalValue))
                )
            )
            {
                var single = await CreateIfNotExist(missing);
                if (single is not null)
                {
                    upserted.Add(single);
                }
            }
        }

        return upserted;
    }

    private async Task<List<Pseudonym>> SequentialFallbackAsync(IReadOnlyList<Pseudonym> pseudonyms)
    {
        var results = new List<Pseudonym>(pseudonyms.Count);
        foreach (var pseudonym in pseudonyms)
        {
            var single = await CreateIfNotExist(pseudonym);
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
    private static string BuildBatchUpsertSql(
        IReadOnlyList<Pseudonym> pseudonyms,
        out object[] parameters
    )
    {
        var values = new StringBuilder();
        parameters = new object[pseudonyms.Count * 3];
        for (var i = 0; i < pseudonyms.Count; i++)
        {
            if (i > 0)
            {
                values.Append(", ");
            }

            var baseIndex = i * 3;
            values
                .Append('(')
                .Append('{')
                .Append(baseIndex)
                .Append("}, {")
                .Append(baseIndex + 1)
                .Append("}, {")
                .Append(baseIndex + 2)
                .Append("})");

            parameters[baseIndex] = pseudonyms[i].NamespaceName;
            parameters[baseIndex + 1] = pseudonyms[i].OriginalValue;
            parameters[baseIndex + 2] = pseudonyms[i].PseudonymValue;
        }

        return $"""
            WITH
                input (namespace_name, original_value, pseudonym_value) AS (
                    VALUES {values}
                ),
                inserted AS (
                    INSERT INTO
                        pseudonyms (namespace_name, original_value, pseudonym_value, created_at, last_updated_at)
                    SELECT namespace_name, original_value, pseudonym_value, NOW(), NOW()
                    FROM input
                    ON CONFLICT (namespace_name, original_value) DO NOTHING
                    RETURNING *
                )
            SELECT *
            FROM inserted
            UNION
            SELECT p.*
            FROM pseudonyms p
            JOIN input i ON p.namespace_name = i.namespace_name AND p.original_value = i.original_value
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
        if (Context.Database.IsNpgsql())
        {
            // Raw SQL, matching the existing precedent in CreateIfNotExist above: a row-value
            // comparison maps directly onto the (namespace_name, created_at, original_value)
            // index as a range scan, which a LINQ-translated equivalent isn't guaranteed to do.
            if (cursor is null)
            {
                return await Context
                    .Pseudonyms.FromSqlInterpolated(
                        $"""
                        SELECT * FROM pseudonyms
                        WHERE namespace_name = {namespaceName}
                        ORDER BY created_at DESC, original_value DESC
                        LIMIT {pageSize}
                        """
                    )
                    .AsNoTracking()
                    .ToListAsync(cancellationToken);
            }

            return await Context
                .Pseudonyms.FromSqlInterpolated(
                    $"""
                    SELECT * FROM pseudonyms
                    WHERE namespace_name = {namespaceName}
                      AND (created_at, original_value) < ({cursor.CreatedAt}, {cursor.OriginalValue})
                    ORDER BY created_at DESC, original_value DESC
                    LIMIT {pageSize}
                    """
                )
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }

        // SQLite (unit tests only, never production scale): page in memory to sidestep any
        // uncertainty about how this provider translates a composite keyset comparison.
        var all = await Context
            .Pseudonyms.AsNoTracking()
            .Where(p => p.NamespaceName == namespaceName)
            .ToListAsync(cancellationToken);

        IEnumerable<Pseudonym> ordered = all.OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.OriginalValue, StringComparer.Ordinal);

        if (cursor is not null)
        {
            ordered = ordered.SkipWhile(p =>
                !(
                    p.CreatedAt < cursor.CreatedAt
                    || (
                        p.CreatedAt == cursor.CreatedAt
                        && string.CompareOrdinal(p.OriginalValue, cursor.OriginalValue) < 0
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
        return await Context
            .Pseudonyms.AsNoTracking()
            .Where(p => p.NamespaceName == namespaceName)
            .LongCountAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, long>> CountAllGroupedByNamespaceAsync(
        CancellationToken cancellationToken
    )
    {
        return await Context
            .Pseudonyms.AsNoTracking()
            .GroupBy(p => p.NamespaceName)
            .Select(g => new { Namespace = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Namespace, x => x.Count, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Pseudonym?> FindByPseudonymValueAsync(
        string namespaceName,
        string pseudonymValue,
        CancellationToken cancellationToken
    )
    {
        return await Context
            .Pseudonyms.AsNoTracking()
            .Where(p => p.NamespaceName == namespaceName && p.PseudonymValue == pseudonymValue)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
