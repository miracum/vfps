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

    // Same reasoning as the indexer test above, but for Generate() specifically - the one place
    // that dispatches between IPseudonymGenerator and IDeterministicPseudonymGenerator. A
    // registered generator implementing neither would only surface here, not via the indexer.
    [Fact]
    public void Generate_ForEveryEnumValue_ShouldReturnANonEmptyPseudonym()
    {
        foreach (var method in Enum.GetValues<PseudonymGenerationMethod>())
        {
            var pseudonymLength = method switch
            {
                PseudonymGenerationMethod.Sha256HexEncoded => 64u,
                PseudonymGenerationMethod.Uuid4 or PseudonymGenerationMethod.Uuid7 => 36u,
                _ => 32u,
            };

            sut.Generate(method, "test", pseudonymLength)
                .Should()
                .NotBeNullOrEmpty($"'{method}' should generate a pseudonym");
        }
    }

    [Theory]
    [InlineData(PseudonymGenerationMethod.Uuid4, 36u)]
    [InlineData(PseudonymGenerationMethod.Uuid7, 36u)]
    [InlineData(PseudonymGenerationMethod.Sha256HexEncoded, 64u)]
    public void GetFixedPseudonymLength_WithFixedLengthMethod_ShouldReturnItsLength(
        PseudonymGenerationMethod method,
        uint expectedLength
    )
    {
        sut.GetFixedPseudonymLength(method).Should().Be(expectedLength);
    }

    [Theory]
    [InlineData(PseudonymGenerationMethod.Unspecified)]
    [InlineData(PseudonymGenerationMethod.SecureRandomBase64UrlEncoded)]
    [InlineData(PseudonymGenerationMethod.FullRandomHexEncoded)]
    [InlineData(PseudonymGenerationMethod.FullRandomBase62Encoded)]
    public void GetFixedPseudonymLength_WithConfigurableLengthMethod_ShouldReturnNull(
        PseudonymGenerationMethod method
    )
    {
        sut.GetFixedPseudonymLength(method).Should().BeNull();
    }
}
