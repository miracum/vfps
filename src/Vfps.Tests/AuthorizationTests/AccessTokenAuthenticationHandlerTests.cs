using System.Security.Claims;
using System.Text.Encodings.Web;
using FakeItEasy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.Data.Models;

namespace Vfps.Tests.AuthorizationTests;

public class AccessTokenAuthenticationHandlerTests
{
    private static readonly AuthorizationConfig Config = new()
    {
        IsEnabled = true,
        AdminRoles = ["admin"],
    };

    private static async Task<(
        AuthenticateResult Result,
        AccessTokenUsageTracker Usage
    )> AuthenticateAsync(string? authorizationHeader, params AccessToken[] tokens)
    {
        var usageTracker = new AccessTokenUsageTracker();
        var optionsMonitor = A.Fake<IOptionsMonitor<AuthenticationSchemeOptions>>();
        A.CallTo(() => optionsMonitor.Get(A<string>._)).Returns(new AuthenticationSchemeOptions());

        var handler = new AccessTokenAuthenticationHandler(
            optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            Options.Create(Config),
            new StaticAccessTokenCache(tokens),
            usageTracker
        );

        var context = new DefaultHttpContext();
        if (authorizationHeader is not null)
        {
            context.Request.Headers.Authorization = authorizationHeader;
        }

        await handler.InitializeAsync(
            new AuthenticationScheme(
                AccessTokenDefaults.AuthenticationScheme,
                displayName: null,
                handlerType: typeof(AccessTokenAuthenticationHandler)
            ),
            context
        );

        return (await handler.AuthenticateAsync(), usageTracker);
    }

    [Fact]
    public async Task WithNoAuthorizationHeader_ShouldNotResolve()
    {
        var (result, _) = await AuthenticateAsync(null);

        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task WithAJwt_ShouldNotResolveRatherThanFail()
    {
        // NoResult, not a failure: another scheme's credential reaching this handler must not
        // bury that scheme's own, more useful result.
        var (result, _) = await AuthenticateAsync(
            "Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln"
        );

        result.None.Should().BeTrue();
        result.Failure.Should().BeNull();
    }

    [Fact]
    public async Task WithAnUnknownToken_ShouldFail()
    {
        var unknown = AccessTokenSecret.Generate(AccessTokenType.Personal);

        var (result, _) = await AuthenticateAsync($"Bearer {unknown.Presented}");

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task WithTheRightIdButTheWrongSecret_ShouldFail()
    {
        var (token, _) = Tokens.Personal();
        var other = AccessTokenSecret.Generate(AccessTokenType.Personal);
        AccessTokenSecret.TryParse(other.Presented, out _, out var otherSecret);

        var (result, _) = await AuthenticateAsync(
            $"Bearer vfps_pat_{token.TokenId}.{otherSecret}",
            token
        );

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task WithAnExpiredToken_ShouldFail()
    {
        var (token, secret) = Tokens.Personal(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var (result, usage) = await AuthenticateAsync($"Bearer {secret}", token);

        result.Succeeded.Should().BeFalse();
        usage.DrainPending().Should().BeEmpty();
    }

    [Fact]
    public async Task WithARevokedToken_ShouldFail()
    {
        // Revoked tokens are absent from the cache's snapshot entirely - that is what makes
        // revocation effective - so this is indistinguishable from an unknown token.
        var (token, secret) = Tokens.Personal(revokedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var (result, _) = await AuthenticateAsync($"Bearer {secret}", token);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task WithAPersonalToken_ShouldReplayTheOwnersIdentity()
    {
        var (token, secret) = Tokens.Personal(
            subject: "subject-1",
            email: "user@example.org",
            roles: ["reader", "admin"]
        );

        var (result, usage) = await AuthenticateAsync($"Bearer {secret}", token);

        result.Succeeded.Should().BeTrue();

        var principal = result.Principal!;
        principal.Identity!.IsAuthenticated.Should().BeTrue();
        principal.GetSubject().Should().Be("subject-1");
        principal.GetEmail().Should().Be("user@example.org");
        principal.FindAll("roles").Select(c => c.Value).Should().BeEquivalentTo("reader", "admin");

        // A person, not a service account - so it matches role and email grants, not
        // service-account ones.
        principal.GetServiceAccountName().Should().BeNull();
        principal.IsAccessTokenPrincipal().Should().BeTrue();

        usage.DrainPending().Should().ContainKey(token.Id);
    }

    [Fact]
    public async Task WithAPersonalTokenWhoseOwnerHadNoVerifiedEmail_ShouldCarryNoEmail()
    {
        var (token, secret) = Tokens.Personal(email: null, roles: ["reader"]);

        var (result, _) = await AuthenticateAsync($"Bearer {secret}", token);

        result.Succeeded.Should().BeTrue();
        result.Principal!.GetEmail().Should().BeNull();
    }

    [Fact]
    public async Task WithAServiceAccountToken_ShouldCarryNeitherRolesNorEmail()
    {
        var (token, secret) = Tokens.ForServiceAccount("etl-pipeline");

        var (result, _) = await AuthenticateAsync($"Bearer {secret}", token);

        result.Succeeded.Should().BeTrue();

        var principal = result.Principal!;
        principal.GetServiceAccountName().Should().Be("etl-pipeline");
        principal.GetSubject().Should().Be("service-account:etl-pipeline");
        principal.GetEmail().Should().BeNull();
        principal.FindAll("roles").Should().BeEmpty();
        principal.IsAccessTokenPrincipal().Should().BeTrue();
    }

    [Fact]
    public async Task AServiceAccountToken_ShouldNeverBeAnAdmin()
    {
        // Even when the deployment's admin role is somehow present on the principal: the checker
        // refuses a service account before it ever looks at roles.
        var (token, secret) = Tokens.ForServiceAccount("etl-pipeline");
        var (result, _) = await AuthenticateAsync($"Bearer {secret}", token);

        var checker = new NamespacePermissionChecker(
            Options.Create(Config),
            new StaticNamespaceAccessGrantCache()
        );

        checker.IsAdmin(result.Principal!).Should().BeFalse();
    }

    [Fact]
    public async Task WithAMalformedToken_ShouldFail()
    {
        var (result, _) = await AuthenticateAsync("Bearer vfps_pat_no-secret-here");

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task ShouldIgnoreTheCaseOfTheBearerKeyword()
    {
        var (token, secret) = Tokens.Personal();

        var (result, _) = await AuthenticateAsync($"bearer {secret}", token);

        result.Succeeded.Should().BeTrue();
    }
}
