using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.Data.Models;

namespace Vfps.Tests.ServiceTests;

public class ServiceAccountAppServiceTests : ServiceTestBase
{
    private static readonly AuthorizationConfig EnabledConfig = new()
    {
        IsEnabled = true,
        AdminRoles = ["admin"],
        AccessTokens = new AccessTokenConfig { IsEnabled = true },
    };

    private static ClaimsPrincipal User(string subject, params string[] roles) =>
        new(
            new ClaimsIdentity(
                [new Claim("sub", subject), .. roles.Select(r => new Claim("roles", r))],
                "test"
            )
        );

    private static ClaimsPrincipal Admin => User("admin-subject", "admin");

    private (
        ServiceAccountAppService Sut,
        StaticNamespaceAccessGrantCache GrantCache,
        StaticAccessTokenCache TokenCache
    ) CreateSut()
    {
        var grantCache = new StaticNamespaceAccessGrantCache();
        var tokenCache = new StaticAccessTokenCache();
        var sut = new ServiceAccountAppService(
            new ServiceAccountRepository(InMemoryPseudonymContext),
            new NamespaceAccessGrantRepository(InMemoryPseudonymContext),
            CreatePermissionChecker(EnabledConfig),
            grantCache,
            tokenCache,
            NullLogger<ServiceAccountAppService>.Instance
        );

        return (sut, grantCache, tokenCache);
    }

    [Fact]
    public async Task EveryMethod_AsNonAdmin_ShouldThrowForbidden()
    {
        var (sut, _, _) = CreateSut();
        var nonAdmin = User("subject-1", "reader");

        await FluentActions
            .Awaiting(() => sut.GetAllAsync(nonAdmin, CancellationToken.None))
            .Should()
            .ThrowAsync<ForbiddenException>();

        await FluentActions
            .Awaiting(() => sut.CreateAsync("etl", null, nonAdmin, CancellationToken.None))
            .Should()
            .ThrowAsync<ForbiddenException>();

        await FluentActions
            .Awaiting(() => sut.DeleteAsync("etl", nonAdmin, CancellationToken.None))
            .Should()
            .ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task CreateAsync_ShouldNormalizeTheName()
    {
        var (sut, _, _) = CreateSut();

        var account = await sut.CreateAsync(
            "  ETL-Pipeline  ",
            "  nightly load  ",
            Admin,
            CancellationToken.None
        );

        account.Name.Should().Be("etl-pipeline");
        account.Description.Should().Be("nightly load");
        account.CreatedBy.Should().Be("admin-subject");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a")]
    [InlineData("-leading-hyphen")]
    [InlineData("trailing-hyphen-")]
    [InlineData("has space")]
    [InlineData("has_underscore")]
    [InlineData("has.dot")]
    [InlineData("user@example.org")]
    public async Task CreateAsync_WithAMalformedName_ShouldThrow(string name)
    {
        var (sut, _, _) = CreateSut();

        await FluentActions
            .Awaiting(() => sut.CreateAsync(name, null, Admin, CancellationToken.None))
            .Should()
            .ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_WithATakenName_ShouldThrowAlreadyExists()
    {
        var (sut, _, _) = CreateSut();
        await sut.CreateAsync("etl-pipeline", null, Admin, CancellationToken.None);

        await FluentActions
            .Awaiting(() => sut.CreateAsync("ETL-PIPELINE", null, Admin, CancellationToken.None))
            .Should()
            .ThrowAsync<ServiceAccountAlreadyExistsException>();
    }

    [Fact]
    public async Task DeleteAsync_ForAnUnknownAccount_ShouldThrowNotFound()
    {
        var (sut, _, _) = CreateSut();

        await FluentActions
            .Awaiting(() => sut.DeleteAsync("does-not-exist", Admin, CancellationToken.None))
            .Should()
            .ThrowAsync<ServiceAccountNotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_ShouldTakeTheAccountsGrantsWithIt()
    {
        // Otherwise an account re-created under the same name would silently inherit the deleted
        // one's access - Grantee names it by value, with no foreign key to cascade from.
        var (sut, grantCache, tokenCache) = CreateSut();
        await sut.CreateAsync("etl-pipeline", null, Admin, CancellationToken.None);

        InMemoryPseudonymContext.NamespaceAccessGrants.AddRange(
            Grants.ForServiceAccount("existingNamespace", "etl-pipeline", write: true),
            Grants.ForServiceAccount(null, "etl-pipeline", read: true),
            // A same-named role grant stays: a role and a service account are different
            // grantees that happen to share a string.
            Grants.ForRole("existingNamespace", "etl-pipeline", read: true)
        );
        await InMemoryPseudonymContext.SaveChangesAsync(CancellationToken.None);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        await sut.DeleteAsync("etl-pipeline", Admin, CancellationToken.None);

        var remaining = await InMemoryPseudonymContext
            .NamespaceAccessGrants.AsNoTracking()
            .ToListAsync(CancellationToken.None);

        remaining.Should().ContainSingle().Which.GranteeType.Should().Be(GranteeType.Role);

        // Both caches: the account's grants changed, and so did the set of live tokens.
        grantCache.InvalidateCallCount.Should().Be(1);
        tokenCache.InvalidateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task DeleteAsync_ShouldTakeTheAccountsTokensWithIt()
    {
        var (sut, _, tokenCache) = CreateSut();
        await sut.CreateAsync("etl-pipeline", null, Admin, CancellationToken.None);

        var tokenAppService = new AccessTokenAppService(
            new AccessTokenRepository(InMemoryPseudonymContext),
            new ServiceAccountRepository(InMemoryPseudonymContext),
            CreatePermissionChecker(EnabledConfig),
            tokenCache,
            Options.Create(EnabledConfig),
            NullLogger<AccessTokenAppService>.Instance
        );

        await tokenAppService.CreateForServiceAccountAsync(
            "etl-pipeline",
            "nightly",
            TimeSpan.FromDays(30),
            Admin,
            CancellationToken.None
        );

        await sut.DeleteAsync("etl-pipeline", Admin, CancellationToken.None);

        var remaining = await InMemoryPseudonymContext
            .AccessTokens.AsNoTracking()
            .ToListAsync(CancellationToken.None);
        remaining.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllAsync_ShouldListAccountsByName()
    {
        var (sut, _, _) = CreateSut();
        await sut.CreateAsync("zeta", null, Admin, CancellationToken.None);
        await sut.CreateAsync("alpha", null, Admin, CancellationToken.None);

        var accounts = await sut.GetAllAsync(Admin, CancellationToken.None);

        accounts.Select(a => a.Name).Should().Equal("alpha", "zeta");
    }
}
