using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vfps.Config;

namespace Vfps.Tests.AuthorizationTests;

/// <summary>
/// Exercises the revalidation loop itself, not just the age check behind it: the provider is only
/// worth anything if setting an authenticated state actually starts a loop that later takes that
/// state away again. Driven through the public API a Blazor circuit uses
/// (<c>SetAuthenticationState</c> / <c>AuthenticationStateChanged</c>), so it covers the wiring a
/// test of <see cref="SessionLifetime"/> alone would miss.
/// </summary>
public class SessionRevalidatingAuthenticationStateProviderTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    private static SessionRevalidatingAuthenticationStateProvider Create(TimeSpan maxSessionAge)
    {
        var config = new AuthorizationConfig
        {
            IsEnabled = true,
            MaxSessionAge = maxSessionAge,
            SessionRevalidationInterval = Interval,
        };

        return new SessionRevalidatingAuthenticationStateProvider(
            NullLoggerFactory.Instance,
            Options.Create(config),
            NullLogger<SessionRevalidatingAuthenticationStateProvider>.Instance
        );
    }

    private static AuthenticationState SignedInAt(DateTimeOffset signedInAt) =>
        new(
            new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim("sub", "someone"), SessionLifetime.StampFor(signedInAt)],
                    "TestAuth"
                )
            )
        );

    [Fact]
    public async Task AnOpenCircuit_WithASignInPastTheLimit_ShouldHaveItsAuthenticatedStateDropped()
    {
        using var sut = Create(TimeSpan.FromMinutes(30));
        var signedOut = new TaskCompletionSource<AuthenticationState>();
        sut.AuthenticationStateChanged += async task =>
        {
            var state = await task;
            if (state.User.Identity?.IsAuthenticated != true)
            {
                signedOut.TrySetResult(state);
            }
        };

        // A circuit connecting with a principal whose sign-in is already older than the limit.
        sut.SetAuthenticationState(Task.FromResult(SignedInAt(DateTimeOffset.UtcNow.AddHours(-1))));

        var completed = await Task.WhenAny(signedOut.Task, Task.Delay(TimeSpan.FromSeconds(15)));

        completed.Should().Be(signedOut.Task, "the revalidation loop should have signed it out");
        (await signedOut.Task).User.Identity?.IsAuthenticated.Should().NotBe(true);
    }

    [Fact]
    public async Task AnOpenCircuit_WithAFreshSignIn_ShouldBeLeftAlone()
    {
        using var sut = Create(TimeSpan.FromMinutes(30));
        var signedOut = new TaskCompletionSource<AuthenticationState>();
        sut.AuthenticationStateChanged += async task =>
        {
            var state = await task;
            if (state.User.Identity?.IsAuthenticated != true)
            {
                signedOut.TrySetResult(state);
            }
        };

        sut.SetAuthenticationState(Task.FromResult(SignedInAt(DateTimeOffset.UtcNow)));

        // Several revalidation intervals' worth of grace - long enough that a loop wrongly
        // deciding this session was stale would have done it by now.
        var completed = await Task.WhenAny(signedOut.Task, Task.Delay(Interval * 10));

        completed.Should().NotBe(signedOut.Task);
        var state = await sut.GetAuthenticationStateAsync();
        state.User.Identity?.IsAuthenticated.Should().BeTrue();
    }
}
