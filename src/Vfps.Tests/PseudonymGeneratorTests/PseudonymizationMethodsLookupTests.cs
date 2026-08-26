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

    // Same reasoning as the indexer test above, but for Generate() specifically.
    [Fact]
    public void Generate_ForEveryEnumValue_ShouldReturnANonEmptyPseudonym()
    {
        foreach (var method in Enum.GetValues<PseudonymGenerationMethod>())
        {
            var pseudonymLength = method switch
            {
                PseudonymGenerationMethod.Uuid4 or PseudonymGenerationMethod.Uuid7 => 36u,
                _ => 32u,
            };

            sut.Generate(method, pseudonymLength)
                .Should()
                .NotBeNullOrEmpty($"'{method}' should generate a pseudonym");
        }
    }

    // The former SHA-256 method's enum number (2) is `reserved` in the proto, not reused - an
    // existing namespace created before its removal would still have this stored. Generate() must
    // fail loudly and clearly for it rather than silently producing something or throwing a raw
    // KeyNotFoundException.
    [Fact]
    public void Generate_ForRemovedSha256Method_ShouldThrowPseudonymGenerationMethodNotSupportedException()
    {
        var removedMethod = (PseudonymGenerationMethod)2;

        var act = () => sut.Generate(removedMethod, 64u);

        act.Should()
            .Throw<PseudonymGenerationMethodNotSupportedException>()
            .Which.Method.Should()
            .Be(removedMethod);
    }

    [Theory]
    [InlineData(PseudonymGenerationMethod.Uuid4, 36u)]
    [InlineData(PseudonymGenerationMethod.Uuid7, 36u)]
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
    [InlineData(PseudonymGenerationMethod.FullRandomBase32Encoded)]
    public void GetFixedPseudonymLength_WithConfigurableLengthMethod_ShouldReturnNull(
        PseudonymGenerationMethod method
    )
    {
        sut.GetFixedPseudonymLength(method).Should().BeNull();
    }
}
