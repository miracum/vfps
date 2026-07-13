using System.Text.RegularExpressions;
using Vfps.PseudonymGenerators;

namespace Vfps.Tests.PseudonymGeneratorTests;

public partial class FullRandomHexEncodedGeneratorTests
{
    private readonly FullRandomHexEncodedGenerator sut = new();

    [Theory]
    [InlineData(16u)]
    [InlineData(32u)]
    [InlineData(64u)]
    [InlineData(17u)] // odd lengths must still be hit exactly, not just rounded to a byte count.
    public void GeneratePseudonym_WithGivenLength_ShouldGenerateExactLengthHexString(
        uint pseudonymLength
    )
    {
        var generated = sut.GeneratePseudonym(pseudonymLength);

        generated.Should().HaveLength((int)pseudonymLength);
        HexCharacters().IsMatch(generated).Should().BeTrue();
    }

    [Fact]
    public void GeneratePseudonym_CalledTwice_ShouldGenerateDifferentValues()
    {
        var first = sut.GeneratePseudonym(64);
        var second = sut.GeneratePseudonym(64);

        first.Should().NotBe(second);
    }

    [GeneratedRegex("^[0-9a-f]*$")]
    private static partial Regex HexCharacters();
}
