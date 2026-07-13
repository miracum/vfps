using Vfps.PseudonymGenerators;

namespace Vfps.Tests.PseudonymGeneratorTests;

public class CryptoRandomBase64UrlEncodedGeneratorTests
{
    private readonly CryptoRandomBase64UrlEncodedGenerator sut = new();

    [Fact]
    public void GeneratePseudonym_CalledTwice_ShouldGenerateDifferentValues()
    {
        var first = sut.GeneratePseudonym(32);
        var second = sut.GeneratePseudonym(32);

        first.Should().NotBe(second);
    }
}
