using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.Data.Models;

namespace Vfps.Tests.ServiceTests;

public class AccessTokenAppServiceTests : ServiceTestBase
{
    private static AuthorizationConfig EnabledConfig =>
        new()
        {
            IsEnabled = true,
            AdminRoles = ["admin"],
            AccessTokens = new AccessTokenConfig
            {
                IsEnabled = true,
                DefaultLifetime = TimeSpan.FromDays(90),
                MaximumLifetime = TimeSpan.FromDays(365),
            },
        };

    private static ClaimsPrincipal User(
        string subject,
        string? email = null,
        bool isEmailVerified = true,
        params string[] roles
    )
    {
        List<Claim> claims = [new("sub", subject), .. roles.Select(r => new Claim("roles", r))];

        if (email is not null)
        {
            claims.Add(new Claim("email", email));
            claims.Add(new Claim("email_verified", isEmailVerified ? "true" : "false"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static ClaimsPrincipal Admin => User("admin-subject", roles: "admin");

    private (AccessTokenAppService Sut, StaticAccessTokenCache Cache) CreateSut(
        AuthorizationConfig? config = null
    )
    {
        config ??= EnabledConfig;
        var cache = new StaticAccessTokenCache();
        var sut = new AccessTokenAppService(
            new AccessTokenRepository(ContextFactory),
            new ServiceAccountRepository(ContextFactory),
            CreatePermissionChecker(config),
            cache,
            Options.Create(config),
            NullLogger<AccessTokenAppService>.Instance
        );

        return (sut, cache);
    }

    private async Task<ServiceAccount> SeedServiceAccountAsync(string name)
    {
        var account = new ServiceAccount
        {
            Name = name,
            CreatedBy = "admin-subject",
            CreatedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };

        InMemoryPseudonymContext.ServiceAccounts.Add(account);
        await InMemoryPseudonymContext.SaveChangesAsync(CancellationToken.None);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        return account;
    }

    [Fact]
    public async Task CreatePersonalAsync_ShouldCaptureTheCallersIdentity()
    {
        var (sut, cache) = CreateSut();
        var user = User("subject-1", "User@Example.org", true, "reader", "writer");

        var issued = await sut.CreatePersonalAsync(
            "my token",
            TimeSpan.FromDays(30),
            user,
            CancellationToken.None
        );

        issued.Token.TokenType.Should().Be(AccessTokenType.Personal);
        issued.Token.Subject.Should().Be("subject-1");
        issued.Token.Roles.Should().BeEquivalentTo("reader", "writer");
        // Lower-cased, like the email grants it has to match.
        issued.Token.Email.Should().Be("user@example.org");
        issued.Token.ServiceAccountName.Should().BeNull();
        issued
            .Token.ExpiresAt.Should()
            .BeCloseTo(DateTimeOffset.UtcNow.AddDays(30), TimeSpan.FromMinutes(1));

        // The new token has to work on this replica at once, not only once the snapshot ages out.
        cache.InvalidateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task CreatePersonalAsync_ShouldReturnASecretThatVerifiesAgainstTheStoredHash()
    {
        var (sut, _) = CreateSut();

        var issued = await sut.CreatePersonalAsync(
            "my token",
            TimeSpan.FromDays(30),
            User("subject-1"),
            CancellationToken.None
        );

        issued.Secret.Should().StartWith("vfps_pat_");
        AccessTokenSecret
            .TryParse(issued.Secret, out var tokenId, out var secret)
            .Should()
            .BeTrue();
        tokenId.Should().Be(issued.Token.TokenId);

        var stored = await InMemoryPseudonymContext.AccessTokens.SingleAsync(
            t => t.Id == issued.Token.Id,
            CancellationToken.None
        );
        AccessTokenSecret.Verify(secret, stored.TokenHash).Should().BeTrue();
    }

    [Fact]
    public async Task CreatePersonalAsync_WithAnUnverifiedEmail_ShouldCaptureNoEmail()
    {
        // Matching what a permission check would honour for this user in a browser session: an
        // unverified address never matches an email grant, so a token must not carry one either.
        var (sut, _) = CreateSut();

        var issued = await sut.CreatePersonalAsync(
            "my token",
            TimeSpan.FromDays(30),
            User("subject-1", "user@example.org", isEmailVerified: false),
            CancellationToken.None
        );

        issued.Token.Email.Should().BeNull();
    }

    [Fact]
    public async Task CreatePersonalAsync_AsAnonymous_ShouldThrowForbidden()
    {
        var (sut, _) = CreateSut();

        await FluentActions
            .Awaiting(() =>
                sut.CreatePersonalAsync(
                    "my token",
                    TimeSpan.FromDays(30),
                    new ClaimsPrincipal(new ClaimsIdentity()),
                    CancellationToken.None
                )
            )
            .Should()
            .ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task CreatePersonalAsync_FromATokenAuthenticatedRequest_ShouldThrowForbidden()
    {
        // Otherwise a leaked token could renew itself indefinitely and its expiry - the bound
        // that works even when nobody has noticed the leak - would stop meaning anything.
        var (sut, _) = CreateSut();
        var tokenPrincipal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim("sub", "subject-1"), new Claim(VfpsClaimTypes.TokenId, "abc")],
                "test"
            )
        );

        await FluentActions
            .Awaiting(() =>
                sut.CreatePersonalAsync(
                    "my token",
                    TimeSpan.FromDays(30),
                    tokenPrincipal,
                    CancellationToken.None
                )
            )
            .Should()
            .ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task EveryMethod_WhenAccessTokensAreDisabled_ShouldThrowForbidden()
    {
        var config = EnabledConfig;
        config.AccessTokens.IsEnabled = false;
        var (sut, _) = CreateSut(config);

        await FluentActions
            .Awaiting(() => sut.GetMineAsync(User("subject-1"), CancellationToken.None))
            .Should()
            .ThrowAsync<ForbiddenException>();

        await FluentActions
            .Awaiting(() =>
                sut.CreatePersonalAsync(
                    "my token",
                    TimeSpan.FromDays(30),
                    User("subject-1"),
                    CancellationToken.None
                )
            )
            .Should()
            .ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task EveryMethod_WhenAuthorizationIsDisabled_ShouldThrowForbidden()
    {
        // There is no identity to issue a token for when authorization is off, and every caller
        // already has full access anyway.
        var config = EnabledConfig;
        config.IsEnabled = false;
        var (sut, _) = CreateSut(config);

        await FluentActions
            .Awaiting(() => sut.GetMineAsync(User("subject-1"), CancellationToken.None))
            .Should()
            .ThrowAsync<ForbiddenException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    public async Task CreatePersonalAsync_WithALifetimeOutOfRange_ShouldThrow(int days)
    {
        var (sut, _) = CreateSut();

        await FluentActions
            .Awaiting(() =>
                sut.CreatePersonalAsync(
                    "my token",
                    TimeSpan.FromDays(days),
                    User("subject-1"),
                    CancellationToken.None
                )
            )
            .Should()
            .ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreatePersonalAsync_WithAnUncappedMaximumLifetime_ShouldAllowAnyFuture()
    {
        var config = EnabledConfig;
        config.AccessTokens.MaximumLifetime = TimeSpan.Zero;
        var (sut, _) = CreateSut(config);

        var issued = await sut.CreatePersonalAsync(
            "long-lived",
            TimeSpan.FromDays(3650),
            User("subject-1"),
            CancellationToken.None
        );

        issued.Token.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(3000));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreatePersonalAsync_WithoutAName_ShouldThrow(string name)
    {
        var (sut, _) = CreateSut();

        await FluentActions
            .Awaiting(() =>
                sut.CreatePersonalAsync(
                    name,
                    TimeSpan.FromDays(30),
                    User("subject-1"),
                    CancellationToken.None
                )
            )
            .Should()
            .ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetMineAsync_ShouldReturnOnlyTheCallersOwnTokens()
    {
        var (sut, _) = CreateSut();
        var mine = User("subject-1");
        var theirs = User("subject-2");

        await sut.CreatePersonalAsync("mine", TimeSpan.FromDays(1), mine, CancellationToken.None);
        await sut.CreatePersonalAsync(
            "theirs",
            TimeSpan.FromDays(1),
            theirs,
            CancellationToken.None
        );

        var tokens = await sut.GetMineAsync(mine, CancellationToken.None);

        tokens.Should().ContainSingle().Which.Name.Should().Be("mine");
    }

    [Fact]
    public async Task GetAllAsync_AsNonAdmin_ShouldThrowForbidden()
    {
        var (sut, _) = CreateSut();

        await FluentActions
            .Awaiting(() =>
                sut.GetAllAsync(User("subject-1", roles: "reader"), CancellationToken.None)
            )
            .Should()
            .ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task RevokeAsync_ByItsOwner_ShouldRevokeAndInvalidateTheCache()
    {
        var (sut, cache) = CreateSut();
        var owner = User("subject-1");
        var issued = await sut.CreatePersonalAsync(
            "mine",
            TimeSpan.FromDays(30),
            owner,
            CancellationToken.None
        );

        await sut.RevokeAsync(issued.Token.Id, owner, CancellationToken.None);

        var stored = await InMemoryPseudonymContext.AccessTokens.SingleAsync(
            t => t.Id == issued.Token.Id,
            CancellationToken.None
        );
        stored.RevokedAt.Should().NotBeNull();

        // Once for the create, once for the revoke.
        cache.InvalidateCallCount.Should().Be(2);
    }

    [Fact]
    public async Task RevokeAsync_Twice_ShouldNotMoveTheRevocationTimestamp()
    {
        // When a credential stopped working is an audit fact - a second click must not rewrite it.
        var (sut, _) = CreateSut();
        var owner = User("subject-1");
        var issued = await sut.CreatePersonalAsync(
            "mine",
            TimeSpan.FromDays(30),
            owner,
            CancellationToken.None
        );

        await sut.RevokeAsync(issued.Token.Id, owner, CancellationToken.None);
        var firstRevokedAt = (
            await InMemoryPseudonymContext.AccessTokens.SingleAsync(
                t => t.Id == issued.Token.Id,
                CancellationToken.None
            )
        ).RevokedAt;

        await sut.RevokeAsync(issued.Token.Id, owner, CancellationToken.None);

        var stored = await InMemoryPseudonymContext.AccessTokens.SingleAsync(
            t => t.Id == issued.Token.Id,
            CancellationToken.None
        );
        stored.RevokedAt.Should().Be(firstRevokedAt);
    }

    [Fact]
    public async Task RevokeAsync_BySomebodyElse_ShouldThrowForbidden()
    {
        var (sut, _) = CreateSut();
        var issued = await sut.CreatePersonalAsync(
            "mine",
            TimeSpan.FromDays(30),
            User("subject-1"),
            CancellationToken.None
        );

        await FluentActions
            .Awaiting(() =>
                sut.RevokeAsync(issued.Token.Id, User("subject-2"), CancellationToken.None)
            )
            .Should()
            .ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task RevokeAsync_ByAnAdmin_ShouldBeAllowed()
    {
        // Incident response: an admin has to be able to kill somebody else's leaked credential.
        var (sut, _) = CreateSut();
        var issued = await sut.CreatePersonalAsync(
            "mine",
            TimeSpan.FromDays(30),
            User("subject-1"),
            CancellationToken.None
        );

        await sut.RevokeAsync(issued.Token.Id, Admin, CancellationToken.None);

        var stored = await InMemoryPseudonymContext.AccessTokens.SingleAsync(
            t => t.Id == issued.Token.Id,
            CancellationToken.None
        );
        stored.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RevokeAsync_ForAnUnknownToken_ShouldThrowNotFound()
    {
        var (sut, _) = CreateSut();

        await FluentActions
            .Awaiting(() => sut.RevokeAsync(Guid.NewGuid(), Admin, CancellationToken.None))
            .Should()
            .ThrowAsync<AccessTokenNotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_BySomebodyElse_ShouldThrowForbidden()
    {
        var (sut, _) = CreateSut();
        var issued = await sut.CreatePersonalAsync(
            "mine",
            TimeSpan.FromDays(30),
            User("subject-1"),
            CancellationToken.None
        );

        await FluentActions
            .Awaiting(() =>
                sut.DeleteAsync(issued.Token.Id, User("subject-2"), CancellationToken.None)
            )
            .Should()
            .ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task CreateForServiceAccountAsync_ShouldCarryNoPersonalIdentity()
    {
        var (sut, _) = CreateSut();
        await SeedServiceAccountAsync("etl-pipeline");

        var issued = await sut.CreateForServiceAccountAsync(
            "etl-pipeline",
            "nightly",
            TimeSpan.FromDays(30),
            Admin,
            CancellationToken.None
        );

        issued.Secret.Should().StartWith("vfps_sat_");
        issued.Token.TokenType.Should().Be(AccessTokenType.ServiceAccount);
        issued.Token.ServiceAccountName.Should().Be("etl-pipeline");
        issued.Token.Subject.Should().BeNull();
        issued.Token.Email.Should().BeNull();
        issued.Token.Roles.Should().BeEmpty();
        // Who issued it is still recorded, even though the token isn't theirs.
        issued.Token.CreatedBy.Should().Be("admin-subject");
    }

    [Fact]
    public async Task CreateForServiceAccountAsync_AsNonAdmin_ShouldThrowForbidden()
    {
        var (sut, _) = CreateSut();
        await SeedServiceAccountAsync("etl-pipeline");

        await FluentActions
            .Awaiting(() =>
                sut.CreateForServiceAccountAsync(
                    "etl-pipeline",
                    "nightly",
                    TimeSpan.FromDays(30),
                    User("subject-1", roles: "reader"),
                    CancellationToken.None
                )
            )
            .Should()
            .ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task CreateForServiceAccountAsync_ForAnUnknownAccount_ShouldThrowNotFound()
    {
        var (sut, _) = CreateSut();

        await FluentActions
            .Awaiting(() =>
                sut.CreateForServiceAccountAsync(
                    "does-not-exist",
                    "nightly",
                    TimeSpan.FromDays(30),
                    Admin,
                    CancellationToken.None
                )
            )
            .Should()
            .ThrowAsync<ServiceAccountNotFoundException>();
    }

    [Fact]
    public async Task RevokeAsync_ForAServiceAccountToken_ShouldBeAdminOnly()
    {
        // A service-account token has no owner, so there is no non-admin who may manage it.
        var (sut, _) = CreateSut();
        await SeedServiceAccountAsync("etl-pipeline");
        var issued = await sut.CreateForServiceAccountAsync(
            "etl-pipeline",
            "nightly",
            TimeSpan.FromDays(30),
            Admin,
            CancellationToken.None
        );

        await FluentActions
            .Awaiting(() =>
                sut.RevokeAsync(issued.Token.Id, User("subject-1"), CancellationToken.None)
            )
            .Should()
            .ThrowAsync<ForbiddenException>();

        await sut.RevokeAsync(issued.Token.Id, Admin, CancellationToken.None);
    }
}
