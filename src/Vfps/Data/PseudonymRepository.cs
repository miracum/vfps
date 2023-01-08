using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;
using Prometheus;

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
                var pseudonyms = await Context.Pseudonyms
                    .FromSqlRaw(
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
}
