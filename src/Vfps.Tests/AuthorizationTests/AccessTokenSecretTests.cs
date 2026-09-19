using Vfps.Data.Models;

namespace Vfps.Tests.AuthorizationTests;

public class AccessTokenSecretTests
{
    [Theory]
    [InlineData(AccessTokenType.Personal, "vfps_pat_")]
    [InlineData(AccessTokenType.ServiceAccount, "vfps_sat_")]
    public void Generate_ShouldPrefixByTokenType(AccessTokenType tokenType, string expectedPrefix)
    {
        var generated = AccessTokenSecret.Generate(tokenType);

        generated.Presented.Should().StartWith(expectedPrefix);
        generated.Presented.Should().StartWith(AccessTokenSecret.Prefix);
    }

    [Fact]
    public void Generate_ShouldProduceADistinctTokenEveryTime()
    {
        var generated = Enumerable
            .Range(0, 100)
            .Select(_ => AccessTokenSecret.Generate(AccessTokenType.Personal))
            .ToList();

        generated.Select(g => g.Presented).Distinct(StringComparer.Ordinal).Should().HaveCount(100);
        generated.Select(g => g.TokenId).Distinct(StringComparer.Ordinal).Should().HaveCount(100);
    }

    [Fact]
    public void Generated_ShouldRoundTripThroughParseAndVerify()
    {
        var generated = AccessTokenSecret.Generate(AccessTokenType.Personal);

        AccessTokenSecret
            .TryParse(generated.Presented, out var tokenId, out var secret)
            .Should()
            .BeTrue();
        tokenId.Should().Be(generated.TokenId);
        AccessTokenSecret.Verify(secret, generated.Hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_WithADifferentSecret_ShouldFail()
    {
        var generated = AccessTokenSecret.Generate(AccessTokenType.Personal);
        var other = AccessTokenSecret.Generate(AccessTokenType.Personal);

        AccessTokenSecret.TryParse(other.Presented, out _, out var otherSecret).Should().BeTrue();

        AccessTokenSecret.Verify(otherSecret, generated.Hash).Should().BeFalse();
    }

    [Fact]
    public void Generated_WithAnUnderscoreInItsId_ShouldStillRoundTrip()
    {
        // Both halves are base64url, whose alphabet contains "_" - which is why the two are
        // separated by a dot. Generating enough tokens makes an id containing one a near
        // certainty, so this is the regression guard for splitting on the wrong character.
        var withUnderscore = Enumerable
            .Range(0, 200)
            .Select(_ => AccessTokenSecret.Generate(AccessTokenType.Personal))
            .Where(g => g.TokenId.Contains('_', StringComparison.Ordinal))
            .ToList();

        withUnderscore.Should().NotBeEmpty("base64url ids regularly contain an underscore");

        foreach (var generated in withUnderscore)
        {
            AccessTokenSecret
                .TryParse(generated.Presented, out var tokenId, out var secret)
                .Should()
                .BeTrue();
            tokenId.Should().Be(generated.TokenId);
            AccessTokenSecret.Verify(secret, generated.Hash).Should().BeTrue();
        }
    }

    [Fact]
    public void Hash_ShouldNotContainTheSecret()
    {
        var generated = AccessTokenSecret.Generate(AccessTokenType.Personal);
        AccessTokenSecret.TryParse(generated.Presented, out _, out var secret).Should().BeTrue();

        // A SHA-256 digest, and nothing resembling the input: the whole point of storing this
        // rather than the token is that a database dump yields no usable credential.
        generated.Hash.Should().HaveCount(32);
        Convert.ToHexString(generated.Hash).Should().NotContain(secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-vfps-token")]
    // A JWT: three dot-separated segments, and never something this handler should claim.
    [InlineData("eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln")]
    [InlineData("vfps_pat_")]
    [InlineData("vfps_pat_onlyid")]
    [InlineData("vfps_pat_onlyid.")]
    [InlineData("vfps_pat_.secret")]
    [InlineData("vfps_pat_id.secret.extra")]
    public void TryParse_WithSomethingElse_ShouldFail(string? presented)
    {
        AccessTokenSecret.TryParse(presented, out var tokenId, out var secret).Should().BeFalse();
        tokenId.Should().BeEmpty();
        secret.Should().BeEmpty();
    }
}
