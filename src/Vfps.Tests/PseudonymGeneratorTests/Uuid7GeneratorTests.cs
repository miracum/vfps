using Vfps.PseudonymGenerators;

namespace Vfps.Tests.PseudonymGeneratorTests;

public class Uuid7GeneratorTests
{
    private readonly Uuid7Generator sut = new();

    [Fact]
    public void GeneratePseudonym_ShouldGenerateA36CharacterUuid()
    {
        var generated = sut.GeneratePseudonym();
        generated.Should().HaveLength(36);
    }

    [Fact]
    public void GeneratePseudonym_WithLengthOtherThan36_ShouldThrowException()
    {
        sut.Invoking(s => s.GeneratePseudonym(64)).Should().Throw<ArgumentException>();
    }
}
