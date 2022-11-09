using Vfps.PseudonymGenerators;

namespace Vfps.Tests.PseudonymGeneratorTests;

public class HexEncodedSha256HashGeneratorTests
{
    private readonly HexEncodedSha256HashGenerator sut = new();

    [Theory]
    [InlineData("test", "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08")]
    [InlineData("longer Value with an Emoji 👷‍♂️", "74B63BF6DC88DB9A3E54DC7EB2691749F50A3E6F04DC5D1F5EFE54D70EE21759")]
    public void GeneratePseudonym_WithGivenInput_ShouldGenerateExpectedPseudonyms(string input, string expectedPseudonym)
    {
        var generated = sut.GeneratePseudonym(input, 64);

        generated.Should().Be(expectedPseudonym);
    }

    [Fact]
    public void GeneratePseudonym_WithLengthOtherThan64_ShouldThrowException()
    {
        sut.Invoking(s => s.GeneratePseudonym("test", 32))
            .Should().Throw<ArgumentException>();
    }
}
