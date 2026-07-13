using System.Text.RegularExpressions;
using Vfps.PseudonymGenerators;

namespace Vfps.Tests.PseudonymGeneratorTests;

public partial class FullRandomBase62EncodedGeneratorTests
{
    private readonly FullRandomBase62EncodedGenerator sut = new();

    [Theory]
    [InlineData(16u)]
    [InlineData(32u)]
    [InlineData(64u)]
    [InlineData(17u)]
    public void GeneratePseudonym_WithGivenLength_ShouldGenerateExactLengthBase62String(
        uint pseudonymLength
    )
    {
        var generated = sut.GeneratePseudonym(pseudonymLength);

        generated.Should().HaveLength((int)pseudonymLength);
        Base62Characters().IsMatch(generated).Should().BeTrue();
    }

    [Fact]
    public void GeneratePseudonym_CalledTwice_ShouldGenerateDifferentValues()
    {
        var first = sut.GeneratePseudonym(64);
        var second = sut.GeneratePseudonym(64);

        first.Should().NotBe(second);
    }

    [GeneratedRegex("^[0-9A-Za-z]*$")]
    private static partial Regex Base62Characters();
}
