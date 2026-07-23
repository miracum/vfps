namespace Vfps.Tests.DataTests;

// Unit tests for the pieces of PseudonymRepository that don't need a real database connection.
// The rest of this repository (CreateIfNotExist, ListByNamespaceAsync, CountByNamespaceAsync,
// FindByPseudonymValueAsync) is already exercised indirectly - against SQLite, via
// PseudonymAppServiceTests/PseudonymServiceTests/CsvPseudonymizationJobRunnerTests - but
// BuildBatchUpsertSql is Postgres-only (see CreateIfNotExistBatchAsync's own comment on why the
// SQLite path never reaches it), so it's the one piece those never actually cover. Tested here
// directly as a pure function instead.
public class PseudonymRepositoryTests
{
    private static Data.Models.Pseudonym CreatePseudonym(
        string namespaceName,
        string originalValue
    ) =>
        new()
        {
            NamespaceName = namespaceName,
            OriginalValue = originalValue,
            PseudonymValue = $"pseudonym-of-{originalValue}",
        };

    [Fact]
    public void BuildBatchUpsertSql_WithOnePseudonym_ShouldProduceOneValuesRowAndMatchingParameters()
    {
        var pseudonym = CreatePseudonym("ns", "value1");

        var sql = PseudonymRepository.BuildBatchUpsertSql([pseudonym], out var parameters);

        sql.Should().Contain("VALUES ({0}, {1}, {2})");
        sql.Should().Contain("INSERT INTO");
        sql.Should().Contain("pseudonyms (namespace_name, original_value, pseudonym_value");
        sql.Should().Contain("ON CONFLICT (namespace_name, original_value) DO NOTHING");
        parameters
            .Should()
            .BeEquivalentTo(["ns", "value1", "pseudonym-of-value1"], o => o.WithStrictOrdering());
    }

    [Fact]
    public void BuildBatchUpsertSql_WithMultiplePseudonyms_ShouldIndexEachRowsPlaceholdersInOrder()
    {
        var pseudonyms = new[]
        {
            CreatePseudonym("ns1", "value1"),
            CreatePseudonym("ns2", "value2"),
            CreatePseudonym("ns3", "value3"),
        };

        var sql = PseudonymRepository.BuildBatchUpsertSql(pseudonyms, out var parameters);

        // Each row's three placeholders must be contiguous and non-overlapping, in the same
        // order as the input list - a single off-by-one here would silently pair one row's
        // namespace with a different row's original/pseudonym value.
        sql.Should().Contain("VALUES ({0}, {1}, {2}), ({3}, {4}, {5}), ({6}, {7}, {8})");
        parameters
            .Should()
            .BeEquivalentTo(
                [
                    "ns1",
                    "value1",
                    "pseudonym-of-value1",
                    "ns2",
                    "value2",
                    "pseudonym-of-value2",
                    "ns3",
                    "value3",
                    "pseudonym-of-value3",
                ],
                o => o.WithStrictOrdering()
            );
    }

    [Fact]
    public void BuildBatchUpsertSql_ShouldDedupeResultRowsWithUnionNotUnionAll()
    {
        // UNION (not UNION ALL) is load-bearing - see the method's own doc comment on why
        // duplicate keys within one batch are tolerated rather than required to be pre-deduped.
        var sql = PseudonymRepository.BuildBatchUpsertSql([CreatePseudonym("ns", "value1")], out _);

        sql.Should().Contain("\nUNION\n");
        sql.Should().NotContain("UNION ALL");
    }
}
