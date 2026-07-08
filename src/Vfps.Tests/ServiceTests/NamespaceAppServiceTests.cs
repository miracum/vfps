using System.Security.Claims;
using Vfps.Config;

namespace Vfps.Tests.ServiceTests;

public class NamespaceAppServiceTests : ServiceTestBase
{
    private static ClaimsPrincipal UserWithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim("roles", r))));

    [Fact]
    public async Task CreateAsync_WithAuthorizationEnabledAndNonAdminUser_ShouldThrowForbidden()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = new NamespaceAppService(
            namespaceRepository,
            CreatePermissionChecker(
                new AuthorizationConfig { IsEnabled = true, AdminRoles = ["admin"] }
            )
        );

        var act = () =>
            sut.CreateAsync(
                new Data.Models.Namespace { Name = "should-not-be-created", PseudonymLength = 16 },
                UserWithRoles("some-other-role"),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task CreateAsync_WithAuthorizationEnabledAndAdminUser_ShouldSucceed()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = new NamespaceAppService(
            namespaceRepository,
            CreatePermissionChecker(
                new AuthorizationConfig { IsEnabled = true, AdminRoles = ["admin"] }
            )
        );

        var created = await sut.CreateAsync(
            new Data.Models.Namespace { Name = "created-by-admin", PseudonymLength = 16 },
            UserWithRoles("admin"),
            CancellationToken.None
        );

        created.Name.Should().Be("created-by-admin");
    }

    [Fact]
    public async Task GetAllAsync_WithAuthorizationEnabled_ShouldOnlyReturnNamespacesUserCanRead()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = new NamespaceAppService(
            namespaceRepository,
            CreatePermissionChecker(
                new AuthorizationConfig
                {
                    IsEnabled = true,
                    NamespaceRules =
                    [
                        new NamespaceRule
                        {
                            Namespace = "existingNamespace",
                            ReadRoles = ["can-read-existing"],
                        },
                    ],
                }
            )
        );

        var result = await sut.GetAllAsync(
            UserWithRoles("can-read-existing"),
            CancellationToken.None
        );

        result.Should().ContainSingle(n => n.Name == "existingNamespace");
        result.Should().NotContain(n => n.Name == "emptyNamespace");
    }
}
