using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Options;
using Vfps.Config;

namespace Vfps.Authorization;

/// <summary>
/// Replaces Blazor's default ServerAuthenticationStateProvider, which hands a circuit the
/// principal it was created with and never looks at it again. This one re-checks that principal
/// on an interval and, once the sign-in behind it is too old (see <see cref="SessionLifetime"/>),
/// drops the circuit's authenticated state.
///
/// What the user sees then is a normal trip through the login flow - Routes.razor renders
/// RedirectToLogin for an unauthenticated state - which completes silently and lands back on the
/// same page whenever the IdP session is still good, and stops at the IdP's login form when it
/// isn't. That is the point: the round trip is what asks the IdP the questions this app cannot
/// answer from inside a circuit, so a disabled account or a withdrawn role takes effect there.
/// </summary>
internal sealed class SessionRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IOptions<AuthorizationConfig> options,
    ILogger<SessionRevalidatingAuthenticationStateProvider> logger
) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    // A floor rather than the configured value as-is: Task.Delay throws on a negative interval,
    // and the base class polls in a tight loop on a zero one.
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    protected override TimeSpan RevalidationInterval =>
        options.Value.SessionRevalidationInterval < MinimumInterval
            ? MinimumInterval
            : options.Value.SessionRevalidationInterval;

    protected override Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken
    )
    {
        var user = authenticationState.User;
        if (!SessionLifetime.HasExpired(user, options.Value.MaxSessionAge, DateTimeOffset.UtcNow))
        {
            return Task.FromResult(true);
        }

        logger.LogInformation(
            "Dropping the authenticated state of an open circuit for {Subject}: its sign-in is "
                + "older than Authorization:MaxSessionAge ({MaxSessionAge}). The browser goes "
                + "back through the login flow, which re-authenticates silently if the IdP "
                + "session is still valid.",
            user.GetSubject(),
            options.Value.MaxSessionAge
        );

        return Task.FromResult(false);
    }
}
