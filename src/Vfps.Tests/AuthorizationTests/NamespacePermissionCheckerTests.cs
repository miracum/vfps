using System.Security.Claims;
using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.Data.Models;

namespace Vfps.Tests.AuthorizationTests;

public class NamespacePermissionCheckerTests
{
    private static ClaimsPrincipal UserWithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim("roles", r))));

    // Carries email_verified, as a real IdP does for a confirmed address: an email grant only
    // matches a verified one (see ClaimsPrincipalExtensions.GetEmail).
    private static ClaimsPrincipal UserWithEmail(string email, params string[] roles) =>
        UserWithEmail(email, isVerified: true, roles);

    private static ClaimsPrincipal UserWithEmail(
        string email,
        bool isVerified,
        params string[] roles
    ) =>
        new(
            new ClaimsIdentity(
                roles
                    .Select(r => new Claim("roles", r))
                    .Append(new Claim("email", email))
                    .Append(new Claim("email_verified", isVerified ? "true" : "false"))
            )
        );

    private static ClaimsPrincipal UserWithUnverifiableEmail(string email) =>
        new(new ClaimsIdentity([new Claim("email", email)]));

    private static NamespacePermissionChecker CreateSut(
        AuthorizationConfig config,
        params NamespaceAccessGrant[] grants
    ) => new(Options.Create(config), new StaticNamespaceAccessGrantCache(grants));

    [Fact]
    public async Task AllChecks_WhenAuthorizationDisabled_ShouldAllowEveryone()
    {
        var sut = CreateSut(new AuthorizationConfig { IsEnabled = false });
        var anonymous = new ClaimsPrincipal();

        sut.IsAdmin(anonymous).Should().BeTrue();
        (await sut.HasReadAccessAsync(anonymous, "any-namespace", CancellationToken.None))
            .Should()
            .BeTrue();
        (await sut.HasWriteAccessAsync(anonymous, "any-namespace", CancellationToken.None))
            .Should()
            .BeTrue();
        (await sut.HasReverseLookupAccessAsync(anonymous, "any-namespace", CancellationToken.None))
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task AllChecks_WhenEnabledAndUserHasNoRoles_ShouldDenyEverything()
    {
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true, AdminRoles = ["admin"] },
            Grants.ForRole("ns1", "ns1-read", read: true)
        );
        var user = UserWithRoles();

        sut.IsAdmin(user).Should().BeFalse();
        (await sut.HasReadAccessAsync(user, "ns1", CancellationToken.None)).Should().BeFalse();
        (await sut.HasWriteAccessAsync(user, "ns1", CancellationToken.None)).Should().BeFalse();
        (await sut.HasReverseLookupAccessAsync(user, "ns1", CancellationToken.None))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task IsAdmin_WhenUserHasAdminRole_GrantsAccessToEveryNamespace()
    {
        var sut = CreateSut(new AuthorizationConfig { IsEnabled = true, AdminRoles = ["admin"] });
        var admin = UserWithRoles("admin");
        const string namespaceName = "some-namespace-with-no-grant-at-all";

        sut.IsAdmin(admin).Should().BeTrue();
        (await sut.HasReadAccessAsync(admin, namespaceName, CancellationToken.None))
            .Should()
            .BeTrue();
        (await sut.HasWriteAccessAsync(admin, namespaceName, CancellationToken.None))
            .Should()
            .BeTrue();
        (await sut.HasReverseLookupAccessAsync(admin, namespaceName, CancellationToken.None))
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task HasReadAccess_OnlyGrantedForTheMatchingNamespace()
    {
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("ns1", "ns1-read", read: true)
        );
        var user = UserWithRoles("ns1-read");

        (await sut.HasReadAccessAsync(user, "ns1", CancellationToken.None)).Should().BeTrue();
        (await sut.HasReadAccessAsync(user, "ns2", CancellationToken.None)).Should().BeFalse();
        sut.IsAdmin(user).Should().BeFalse();
    }

    [Fact]
    public async Task GrantWithoutANamespace_AppliesAcrossAllNamespaces()
    {
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole(namespaceName: null, "global-read", read: true)
        );
        var user = UserWithRoles("global-read");

        (await sut.HasReadAccessAsync(user, "ns1", CancellationToken.None)).Should().BeTrue();
        (await sut.HasReadAccessAsync(user, "any-other-namespace", CancellationToken.None))
            .Should()
            .BeTrue();
        (await sut.HasWriteAccessAsync(user, "ns1", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task HasReverseLookupAccess_RequiresTheReverseLookupFlag_ReadAccessIsNotEnough()
    {
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("ns1", "ns1-read", read: true),
            Grants.ForRole("ns1", "ns1-reverse-lookup", reverseLookup: true)
        );
        var readOnlyUser = UserWithRoles("ns1-read");
        var reverseLookupUser = UserWithRoles("ns1-reverse-lookup");

        (await sut.HasReadAccessAsync(readOnlyUser, "ns1", CancellationToken.None))
            .Should()
            .BeTrue();
        (await sut.HasReverseLookupAccessAsync(readOnlyUser, "ns1", CancellationToken.None))
            .Should()
            .BeFalse();
        (await sut.HasReverseLookupAccessAsync(reverseLookupUser, "ns1", CancellationToken.None))
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task EmailGrant_AppliesRegardlessOfTheUsersRoles()
    {
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForEmail("ns1", "user@example.org", read: true, write: true)
        );
        var user = UserWithEmail("user@example.org", "some-unrelated-role");

        (await sut.HasReadAccessAsync(user, "ns1", CancellationToken.None)).Should().BeTrue();
        (await sut.HasWriteAccessAsync(user, "ns1", CancellationToken.None)).Should().BeTrue();
        (await sut.HasReverseLookupAccessAsync(user, "ns1", CancellationToken.None))
            .Should()
            .BeFalse();
        (await sut.HasReadAccessAsync(user, "ns2", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task EmailGrant_MatchesTheEmailClaimCaseInsensitively()
    {
        // Grants are stored lower-cased by NamespaceAccessGrantAppService; an IdP is free to
        // issue the claim in any casing it likes.
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForEmail("ns1", "user@example.org", read: true)
        );

        (
            await sut.HasReadAccessAsync(
                UserWithEmail("User@Example.ORG"),
                "ns1",
                CancellationToken.None
            )
        )
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task EmailGrant_DoesNotApplyToADifferentUser()
    {
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForEmail("ns1", "user@example.org", read: true)
        );

        (
            await sut.HasReadAccessAsync(
                UserWithEmail("someone-else@example.org"),
                "ns1",
                CancellationToken.None
            )
        )
            .Should()
            .BeFalse();

        // A caller with no email claim at all must never match an email grant.
        (await sut.HasReadAccessAsync(UserWithRoles("some-role"), "ns1", CancellationToken.None))
            .Should()
            .BeFalse();
    }

    // An address nobody has confirmed is a self-asserted string. On a realm that allows
    // self-registration or an unverified address change, honouring one would let anyone take
    // over anyone else's grants by typing their address into a profile page.
    [Fact]
    public async Task EmailGrant_DoesNotApplyWhenTheEmailIsNotVerified()
    {
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForEmail("ns1", "user@example.org", read: true, write: true)
        );
        var user = UserWithEmail("user@example.org", isVerified: false);

        (await sut.HasReadAccessAsync(user, "ns1", CancellationToken.None)).Should().BeFalse();
        (await sut.HasWriteAccessAsync(user, "ns1", CancellationToken.None)).Should().BeFalse();
    }

    // An IdP that issues no email_verified claim at all gets no email identity either - failing
    // closed, rather than trusting an address nothing vouches for.
    [Fact]
    public async Task EmailGrant_DoesNotApplyWhenTheIdpIssuesNoVerificationClaim()
    {
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForEmail("ns1", "user@example.org", read: true)
        );

        (
            await sut.HasReadAccessAsync(
                UserWithUnverifiableEmail("user@example.org"),
                "ns1",
                CancellationToken.None
            )
        )
            .Should()
            .BeFalse();
    }

    // Role grants never depended on the email claim and must keep working for the same caller.
    [Fact]
    public async Task RoleGrant_StillAppliesToACallerWhoseEmailIsUnverified()
    {
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("ns1", "reader", read: true)
        );
        var user = UserWithEmail("user@example.org", isVerified: false, "reader");

        (await sut.HasReadAccessAsync(user, "ns1", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Permissions_AreTheUnionOfEveryGrantMatchingTheCaller()
    {
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("ns1", "reader", read: true),
            Grants.ForRole("ns1", "writer", write: true),
            Grants.ForEmail("ns1", "user@example.org", reverseLookup: true),
            Grants.ForRole(namespaceName: null, "reader", read: true)
        );
        var user = UserWithEmail("user@example.org", "reader", "writer");

        var permissions = await sut.ResolveAsync(user, CancellationToken.None);

        permissions.HasReadAccess("ns1").Should().BeTrue();
        permissions.HasWriteAccess("ns1").Should().BeTrue();
        permissions.HasReverseLookupAccess("ns1").Should().BeTrue();

        // The namespace-less "reader" grant still reaches every other namespace, while the
        // ns1-scoped ones don't.
        permissions.HasReadAccess("ns2").Should().BeTrue();
        permissions.HasWriteAccess("ns2").Should().BeFalse();
        permissions.IsAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_ForAnAdmin_ShouldNotEvenReadTheGrants()
    {
        // Admins are unrestricted by definition, so the check short-circuits before the grant
        // source is touched at all - which is what keeps IsAdmin usable synchronously.
        var cache = new ThrowingGrantCache();
        var sut = new NamespacePermissionChecker(
            Options.Create(new AuthorizationConfig { IsEnabled = true, AdminRoles = ["admin"] }),
            cache
        );

        var permissions = await sut.ResolveAsync(UserWithRoles("admin"), CancellationToken.None);

        permissions.IsAdmin.Should().BeTrue();
        permissions.HasWriteAccess("anything").Should().BeTrue();
    }

    private sealed class ThrowingGrantCache : INamespaceAccessGrantCache
    {
        public Task<IReadOnlyList<NamespaceAccessGrant>> GetAllAsync(
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("the grants should not have been read");

        public void Invalidate() { }
    }
}
