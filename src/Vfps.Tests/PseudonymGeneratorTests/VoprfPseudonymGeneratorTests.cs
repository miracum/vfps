using System.Buffers.Text;
using FakeItEasy;
using Microsoft.Extensions.Options;
using Vfps.PseudonymGenerators;
using Vfps.Voprf.Client;

namespace Vfps.Tests.PseudonymGeneratorTests;

/// <summary>
/// The adapter between vfps's generator seam and the VOPRF client. The protocol itself is tested
/// in Vfps.Voprf.Server.Tests against a real server; what matters here is that the namespace's
/// declared pseudonym length matches what the client will actually return, because that is what
/// namespace creation validates against.
/// </summary>
public class VoprfPseudonymGeneratorTests
{
    private static VoprfPseudonymGenerator Create(
        IVoprfPseudonymizer pseudonymizer,
        PseudonymFormat format = PseudonymFormat.Base64Url,
        int length = 64
    ) =>
        new(
            pseudonymizer,
            Options.Create(
                new VoprfClientOptions
                {
                    Address = "https://voprf:8081",
                    PublicKey = new string('a', 64),
                    Format = format,
                    Length = length,
                }
            )
        );

    // The number this reports is what NamespaceAppService rejects a mismatched namespace against,
    // so a wrong answer here means either every VOPRF namespace is refused or every pseudonym it
    // stores is a different length than the namespace claims.
    [Theory]
    [InlineData(PseudonymFormat.Base64Url, 64, 86u)]
    [InlineData(PseudonymFormat.Base64Url, 32, 43u)]
    [InlineData(PseudonymFormat.Base64Url, 16, 22u)]
    [InlineData(PseudonymFormat.Hex, 64, 128u)]
    [InlineData(PseudonymFormat.Hex, 16, 32u)]
    public void FixedPseudonymLength_MatchesWhatTheEncodingActuallyProduces(
        PseudonymFormat format,
        int length,
        uint expected
    )
    {
        var sut = Create(A.Fake<IVoprfPseudonymizer>(), format, length);

        sut.FixedPseudonymLength.Should().Be(expected);

        // Cross-checked against the encoder rather than only against a constant, so a change to
        // either side has to be deliberate.
        var encoded =
            format == PseudonymFormat.Base64Url
                ? Base64Url.EncodeToString(new byte[length])
                : Convert.ToHexStringLower(new byte[length]);
        encoded.Length.Should().Be((int)expected);
    }

    [Fact]
    public async Task GeneratePseudonymsAsync_ReturnsTheClientsValuesInOrder()
    {
        var pseudonymizer = A.Fake<IVoprfPseudonymizer>();
        A.CallTo(() =>
                pseudonymizer.PseudonymizeAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._)
            )
            .Returns<IReadOnlyList<VoprfPseudonym>>([
                new VoprfPseudonym("psn-alice", "v1"),
                new VoprfPseudonym("psn-bob", "v1"),
            ]);

        var sut = Create(pseudonymizer);

        var generated = await sut.GeneratePseudonymsAsync(
            ["alice", "bob"],
            TestContext.Current.CancellationToken
        );

        generated.Should().Equal("psn-alice", "psn-bob");
    }

    // One call for the whole list, not one per value: the client splits it into as many round
    // trips as the server's batch cap needs, and a per-value loop here would defeat that.
    [Fact]
    public async Task GeneratePseudonymsAsync_SendsEveryValueInASingleCall()
    {
        var pseudonymizer = A.Fake<IVoprfPseudonymizer>();
        A.CallTo(() =>
                pseudonymizer.PseudonymizeAsync(A<IReadOnlyList<string>>._, A<CancellationToken>._)
            )
            .Returns<IReadOnlyList<VoprfPseudonym>>([
                .. Enumerable.Range(0, 3).Select(i => new VoprfPseudonym($"p{i}", "v1")),
            ]);

        var sut = Create(pseudonymizer);

        await sut.GeneratePseudonymsAsync(["a", "b", "c"], TestContext.Current.CancellationToken);

        A.CallTo(() =>
                pseudonymizer.PseudonymizeAsync(
                    A<IReadOnlyList<string>>.That.IsSameSequenceAs("a", "b", "c"),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GeneratePseudonymsAsync_WithNoValues_DoesNotCallTheServer()
    {
        var pseudonymizer = A.Fake<IVoprfPseudonymizer>();
        var sut = Create(pseudonymizer);

        var generated = await sut.GeneratePseudonymsAsync(
            [],
            TestContext.Current.CancellationToken
        );

        generated.Should().BeEmpty();
        A.CallTo(pseudonymizer).MustNotHaveHappened();
    }
}
