using Vfps.Data.Models;

namespace Vfps.Tests.DataTests;

public class AccessTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan WarningPeriod = TimeSpan.FromDays(14);

    private static AccessToken TokenLiving(TimeSpan lifetime, DateTimeOffset? revokedAt = null)
    {
        var (token, _) = Tokens.Personal(revokedAt: revokedAt);
        token.CreatedAt = Now;
        token.ExpiresAt = Now + lifetime;
        return token;
    }

    [Fact]
    public void IsExpiringSoonAt_WellBeforeTheWarningPeriod_ShouldBeFalse()
    {
        var token = TokenLiving(TimeSpan.FromDays(90));

        token.IsExpiringSoonAt(Now.AddDays(60), WarningPeriod).Should().BeFalse();
    }

    [Fact]
    public void IsExpiringSoonAt_WithinTheWarningPeriod_ShouldBeTrue()
    {
        var token = TokenLiving(TimeSpan.FromDays(90));

        token.IsExpiringSoonAt(Now.AddDays(80), WarningPeriod).Should().BeTrue();
    }

    [Fact]
    public void IsExpiringSoonAt_ForAShortLivedToken_ShouldOnlyFlagTheLastQuarterOfItsLifetime()
    {
        // A week-long token is shorter than the warning period itself, so without the cap it would
        // be flagged from the moment it was created.
        var token = TokenLiving(TimeSpan.FromDays(8));

        token.IsExpiringSoonAt(Now, WarningPeriod).Should().BeFalse();
        token.IsExpiringSoonAt(Now.AddDays(5), WarningPeriod).Should().BeFalse();
        token.IsExpiringSoonAt(Now.AddDays(7), WarningPeriod).Should().BeTrue();
    }

    [Fact]
    public void IsExpiringSoonAt_OnceExpired_ShouldBeFalse()
    {
        var token = TokenLiving(TimeSpan.FromDays(90));

        token.IsExpiringSoonAt(Now.AddDays(91), WarningPeriod).Should().BeFalse();
    }

    [Fact]
    public void IsExpiringSoonAt_ForARevokedToken_ShouldBeFalse()
    {
        var token = TokenLiving(TimeSpan.FromDays(90), revokedAt: Now.AddDays(1));

        token.IsExpiringSoonAt(Now.AddDays(85), WarningPeriod).Should().BeFalse();
    }

    [Fact]
    public void IsExpiringSoonAt_WithTheWarningDisabled_ShouldBeFalse()
    {
        var token = TokenLiving(TimeSpan.FromDays(90));

        token.IsExpiringSoonAt(Now.AddDays(89), TimeSpan.Zero).Should().BeFalse();
    }
}
