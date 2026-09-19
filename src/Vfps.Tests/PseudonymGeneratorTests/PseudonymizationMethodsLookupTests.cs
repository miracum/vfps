using FakeItEasy;
using Vfps.PseudonymGenerators;

namespace Vfps.Tests.PseudonymGeneratorTests;

public class PseudonymizationMethodsLookupTests
{
    private readonly PseudonymizationMethodsLookup sut = new();

    /// <summary>
    /// Every enum value that is not derived from the original value - the ones a plain
    /// <see cref="IPseudonymGenerator"/> has to cover.
    /// </summary>
    private static IEnumerable<PseudonymGenerationMethod> IndependentMethods =>
        Enum.GetValues<PseudonymGenerationMethod>()
            .Where(method => !PseudonymizationMethodsLookup.IsValueDependent(method));

    // Enumerates the enum itself rather than listing methods individually - a hardcoded list
    // here would have exactly the same "forgot to add the new one" failure mode this test is
    // meant to catch: a PseudonymGenerationMethod value with no registered generator, which
    // would surface as a KeyNotFoundException at pseudonym-creation time instead of at build/CI
    // time. Value-dependent methods are excluded because they deliberately have no
    // IPseudonymGenerator at all; the test below is their half of the same safety net.
    [Fact]
    public void Indexer_ForEveryIndependentEnumValue_ShouldReturnAGenerator()
    {
        foreach (var method in IndependentMethods)
        {
            sut[method].Should().NotBeNull($"'{method}' should have a registered generator");
        }
    }

    // Same reasoning as the indexer test above, but for Generate() specifically.
    [Fact]
    public void Generate_ForEveryIndependentEnumValue_ShouldReturnANonEmptyPseudonym()
    {
        foreach (var method in IndependentMethods)
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

    // The other half of the safety net: a value-dependent method must be reachable through the
    // value-dependent generator, so a newly added one that nothing wires up fails here rather
    // than at pseudonym-creation time.
    [Fact]
    public void GetValueDependentGenerator_ForEveryValueDependentEnumValue_ShouldReturnAGenerator()
    {
        var generator = A.Fake<IValueDependentPseudonymGenerator>();
        var configured = new PseudonymizationMethodsLookup(generator);

        var valueDependent = Enum.GetValues<PseudonymGenerationMethod>()
            .Where(PseudonymizationMethodsLookup.IsValueDependent)
            .ToList();

        valueDependent.Should().NotBeEmpty("VOPRF is value-dependent");

        foreach (var method in valueDependent)
        {
            configured
                .GetValueDependentGenerator(method)
                .Should()
                .BeSameAs(generator, $"'{method}' should resolve to the value-dependent generator");
        }
    }

    [Fact]
    public void Voprf_WithoutAConfiguredServer_IsNotSupported()
    {
        sut.IsSupported(PseudonymGenerationMethod.Voprf).Should().BeFalse();

        var act = () => sut.GetValueDependentGenerator(PseudonymGenerationMethod.Voprf);

        act.Should()
            .Throw<PseudonymGenerationMethodNotSupportedException>()
            .Which.Method.Should()
            .Be(PseudonymGenerationMethod.Voprf);
    }

    [Fact]
    public void Voprf_WithAConfiguredServer_IsSupported()
    {
        var configured = new PseudonymizationMethodsLookup(
            A.Fake<IValueDependentPseudonymGenerator>()
        );

        configured.IsSupported(PseudonymGenerationMethod.Voprf).Should().BeTrue();
    }

    // Generate() is the random path; a value-dependent method has no answer for it, since the
    // pseudonym is a function of a value this overload never receives.
    [Fact]
    public void Generate_ForAValueDependentMethod_ShouldThrow()
    {
        var configured = new PseudonymizationMethodsLookup(
            A.Fake<IValueDependentPseudonymGenerator>()
        );

        var act = () => configured.Generate(PseudonymGenerationMethod.Voprf, 32u);

        act.Should().Throw<PseudonymGenerationMethodNotSupportedException>();
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
