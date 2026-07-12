using Vfps.PseudonymGenerators;

namespace Vfps.Tests.PseudonymGeneratorTests;

public class Uuid7GeneratorTests
{
    private readonly Uuid7Generator sut = new();

    [Theory]
    [InlineData("test")]
    [InlineData("longer Value with an Emoji 👷‍♂️")]
    public void GeneratePseudonym_WithGivenInput_ShouldGenerateExpectedPseudonyms(string input)
    {
        var generated = sut.GeneratePseudonym(input);
        generated.Should().HaveLength(36);
    }

    [Fact]
    public void GeneratePseudonym_WithLengthOtherThan36_ShouldThrowException()
    {
        sut.Invoking(s => s.GeneratePseudonym("test", 64)).Should().Throw<ArgumentException>();
    }
}
