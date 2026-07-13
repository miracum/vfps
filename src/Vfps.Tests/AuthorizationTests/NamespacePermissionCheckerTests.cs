using System.Security.Claims;
using Microsoft.Extensions.Options;
using Vfps.Config;

namespace Vfps.Tests.AuthorizationTests;

public class NamespacePermissionCheckerTests
{
    private static ClaimsPrincipal UserWithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim("roles", r))));

    private static NamespacePermissionChecker CreateSut(AuthorizationConfig config) =>
        new(Options.Create(config));

    [Fact]
    public void AllChecks_WhenAuthorizationDisabled_ShouldAllowEveryone()
    {
        var sut = CreateSut(new AuthorizationConfig { IsEnabled = false });
        var anonymous = new ClaimsPrincipal();

        sut.IsAdmin(anonymous).Should().BeTrue();
        sut.HasReadAccess(anonymous, "any-namespace").Should().BeTrue();
        sut.HasWriteAccess(anonymous, "any-namespace").Should().BeTrue();
        sut.HasReverseLookupAccess(anonymous, "any-namespace").Should().BeTrue();
    }

    [Fact]
    public void AllChecks_WhenEnabledAndUserHasNoRoles_ShouldDenyEverything()
    {
        var sut = CreateSut(
            new AuthorizationConfig
            {
                IsEnabled = true,
                AdminRoles = ["admin"],
                NamespaceRules =
                [
                    new NamespaceRule { Namespace = "ns1", ReadRoles = ["ns1-read"] },
                ],
            }
        );
        var user = UserWithRoles();

        sut.IsAdmin(user).Should().BeFalse();
        sut.HasReadAccess(user, "ns1").Should().BeFalse();
        sut.HasWriteAccess(user, "ns1").Should().BeFalse();
        sut.HasReverseLookupAccess(user, "ns1").Should().BeFalse();
    }

    [Fact]
    public void IsAdmin_WhenUserHasAdminRole_GrantsAccessToEveryNamespace()
    {
        var sut = CreateSut(new AuthorizationConfig { IsEnabled = true, AdminRoles = ["admin"] });
        var admin = UserWithRoles("admin");

        sut.IsAdmin(admin).Should().BeTrue();
        sut.HasReadAccess(admin, "some-namespace-with-no-explicit-rule").Should().BeTrue();
        sut.HasWriteAccess(admin, "some-namespace-with-no-explicit-rule").Should().BeTrue();
        sut.HasReverseLookupAccess(admin, "some-namespace-with-no-explicit-rule").Should().BeTrue();
    }

    [Fact]
    public void HasReadAccess_OnlyGrantedForTheMatchingNamespace()
    {
        var sut = CreateSut(
            new AuthorizationConfig
            {
                IsEnabled = true,
                NamespaceRules =
                [
                    new NamespaceRule { Namespace = "ns1", ReadRoles = ["ns1-read"] },
                ],
            }
        );
        var user = UserWithRoles("ns1-read");

        sut.HasReadAccess(user, "ns1").Should().BeTrue();
        sut.HasReadAccess(user, "ns2").Should().BeFalse();
        sut.IsAdmin(user).Should().BeFalse();
    }

    [Fact]
    public void WildcardNamespaceRule_GrantsAccessAcrossAllNamespaces()
    {
        var sut = CreateSut(
            new AuthorizationConfig
            {
                IsEnabled = true,
                NamespaceRules =
                [
                    new NamespaceRule { Namespace = "*", ReadRoles = ["global-read"] },
                ],
            }
        );
        var user = UserWithRoles("global-read");

        sut.HasReadAccess(user, "ns1").Should().BeTrue();
        sut.HasReadAccess(user, "any-other-namespace").Should().BeTrue();
        sut.HasWriteAccess(user, "ns1").Should().BeFalse();
    }

    [Fact]
    public void HasReverseLookupAccess_RequiresReverseLookupRoleSpecifically_ReadAccessIsNotEnough()
    {
        var sut = CreateSut(
            new AuthorizationConfig
            {
                IsEnabled = true,
                NamespaceRules =
                [
                    new NamespaceRule
                    {
                        Namespace = "ns1",
                        ReadRoles = ["ns1-read"],
                        ReverseLookupRoles = ["ns1-reverse-lookup"],
                    },
                ],
            }
        );
        var readOnlyUser = UserWithRoles("ns1-read");
        var reverseLookupUser = UserWithRoles("ns1-reverse-lookup");

        sut.HasReadAccess(readOnlyUser, "ns1").Should().BeTrue();
        sut.HasReverseLookupAccess(readOnlyUser, "ns1").Should().BeFalse();
        sut.HasReverseLookupAccess(reverseLookupUser, "ns1").Should().BeTrue();
    }
}
