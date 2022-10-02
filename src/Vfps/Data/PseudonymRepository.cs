using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;

namespace Vfps.Data;

/// <inheritdoc/>
public class PseudonymRepository : IPseudonymRepository
{
    private readonly string PostgreSQLInsertCommand =
    @"
        WITH
            cte AS (
            INSERT INTO
                ""Pseudonyms"" (""NamespaceName"", ""OriginalValue"", ""PseudonymValue"", ""CreatedAt"", ""LastUpdatedAt"")
            VALUES
                ({0}, {1}, {2}, NOW(), NOW()) ON CONFLICT (""NamespaceName"", ""OriginalValue"")
            DO NOTHING RETURNING *
            )
        SELECT *
        FROM cte
        UNION
        SELECT *
        FROM ""Pseudonyms""
        WHERE ""NamespaceName""={0} AND ""OriginalValue""={1}
    ";

    private readonly string SqliteInsertCommand =
    @"
        INSERT INTO ""Pseudonyms"" (""NamespaceName"", ""OriginalValue"", ""PseudonymValue"", ""CreatedAt"", ""LastUpdatedAt"")
        VALUES ({0}, {1}, {2}, time('now'), time('now'))
        ON CONFLICT (""NamespaceName"", ""OriginalValue"")
        DO UPDATE SET ""OriginalValue""=excluded.""OriginalValue""
        WHERE ""OriginalValue"" IS excluded.""OriginalValue""
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
            var pseudonyms = await Context.Pseudonyms
                .FromSqlRaw(UpsertCommand, pseudonym.NamespaceName, pseudonym.OriginalValue, pseudonym.PseudonymValue)
                .AsNoTracking()
                .ToListAsync();

            upsertedPseudonym = pseudonyms.FirstOrDefault();
            retryCount--;
        }

        return upsertedPseudonym;
    }
}
