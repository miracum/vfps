using Vfps.PseudonymGenerators;

namespace Vfps.Tests.PseudonymGeneratorTests;

public class PseudonymizationMethodsLookupTests
{
    private readonly PseudonymizationMethodsLookup sut = new();

    // Enumerates the enum itself rather than listing methods individually - a hardcoded list
    // here would have exactly the same "forgot to add the new one" failure mode this test is
    // meant to catch: a PseudonymGenerationMethod value with no registered generator, which
    // would surface as a KeyNotFoundException at pseudonym-creation time instead of at build/CI
    // time.
    [Fact]
    public void Indexer_ForEveryEnumValue_ShouldReturnAGenerator()
    {
        foreach (var method in Enum.GetValues<PseudonymGenerationMethod>())
        {
            sut[method].Should().NotBeNull($"'{method}' should have a registered generator");
        }
    }
}
