using System.Text.RegularExpressions;
using Vfps.PseudonymGenerators;

namespace Vfps.Tests.PseudonymGeneratorTests;

public partial class FullRandomBase32EncodedGeneratorTests
{
    private readonly FullRandomBase32EncodedGenerator sut = new();

    [Theory]
    [InlineData(16u)]
    [InlineData(32u)]
    [InlineData(64u)]
    [InlineData(17u)]
    public void GeneratePseudonym_WithGivenLength_ShouldGenerateExactLengthBase32String(
        uint pseudonymLength
    )
    {
        var generated = sut.GeneratePseudonym(pseudonymLength);

        generated.Should().HaveLength((int)pseudonymLength);
        Base32Characters().IsMatch(generated).Should().BeTrue();
    }

    [Fact]
    public void GeneratePseudonym_CalledTwice_ShouldGenerateDifferentValues()
    {
        var first = sut.GeneratePseudonym(64);
        var second = sut.GeneratePseudonym(64);

        first.Should().NotBe(second);
    }

    // RFC 4648, section 6 alphabet: A-Z and 2-7.
    [GeneratedRegex("^[A-Z2-7]*$")]
    private static partial Regex Base32Characters();
}
