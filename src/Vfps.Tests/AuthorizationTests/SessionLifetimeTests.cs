using System.Security.Claims;

namespace Vfps.Tests.AuthorizationTests;

public class SessionLifetimeTests
{
    private static ClaimsPrincipal UserSignedInAt(DateTimeOffset signedInAt) =>
        new(new ClaimsIdentity([SessionLifetime.StampFor(signedInAt)], "TestAuth"));

    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HasExpired_WithinTheLimit_ShouldBeFalse()
    {
        var user = UserSignedInAt(Now.AddHours(-7));

        SessionLifetime.HasExpired(user, TimeSpan.FromHours(8), Now).Should().BeFalse();
    }

    [Fact]
    public void HasExpired_PastTheLimit_ShouldBeTrue()
    {
        var user = UserSignedInAt(Now.AddHours(-9));

        SessionLifetime.HasExpired(user, TimeSpan.FromHours(8), Now).Should().BeTrue();
    }

    [Fact]
    public void HasExpired_ExactlyAtTheLimit_ShouldBeTrue()
    {
        var user = UserSignedInAt(Now.AddHours(-8));

        SessionLifetime.HasExpired(user, TimeSpan.FromHours(8), Now).Should().BeTrue();
    }

    [Fact]
    public void HasExpired_WithTheLimitSwitchedOff_ShouldBeFalseHoweverOldTheSignIn()
    {
        var user = UserSignedInAt(Now.AddYears(-1));

        SessionLifetime.HasExpired(user, TimeSpan.Zero, Now).Should().BeFalse();
    }

    // A cookie minted before this app started stamping sign-ins, or by any path that doesn't.
    // Signing those out on the spot would turn every session open across a deployment into an
    // immediate redirect, so they are left to the auth cookie's own expiry instead.
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void HasExpired_WithAnUnreadableStamp_ShouldBeFalse(string stamp)
    {
        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(SessionLifetime.SignedInAtClaimType, stamp)], "TestAuth")
        );

        SessionLifetime.HasExpired(user, TimeSpan.FromHours(8), Now).Should().BeFalse();
    }

    [Fact]
    public void HasExpired_WithNoStampAtAll_ShouldBeFalse()
    {
        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("sub", "someone")], "TestAuth")
        );

        SessionLifetime.HasExpired(user, TimeSpan.FromHours(8), Now).Should().BeFalse();
    }

    [Fact]
    public void StampFor_ShouldRoundTripThroughTheClaim()
    {
        var signedInAt = Now.AddMinutes(-42);

        var user = UserSignedInAt(signedInAt);

        // One second before the elapsed time hits the limit it is still valid; one second after
        // the limit it is not - which pins the stamp to the second it was made for.
        SessionLifetime.HasExpired(user, TimeSpan.FromMinutes(43), Now).Should().BeFalse();
        SessionLifetime.HasExpired(user, TimeSpan.FromMinutes(41), Now).Should().BeTrue();
    }
}
