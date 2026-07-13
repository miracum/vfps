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
