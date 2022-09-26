using Vfps.PseudonymGenerators;

namespace Vfps.Tests.PseudonymGeneratorTests;

public class CryptoRandomBase64UrlEncodedGeneratorTests
{
    private readonly CryptoRandomBase64UrlEncodedGenerator sut = new();

    [Fact]
    public void GeneratePseudonym_ShouldGenerateExpectedPseudonyms()
    {
        var input = "test";
        var generated = sut.GeneratePseudonym(input, 32);

        // difficult to assert anything else here given the nature of
        // random values and the base64url-encoding.
        generated.Should().NotBe(input);
    }
}
