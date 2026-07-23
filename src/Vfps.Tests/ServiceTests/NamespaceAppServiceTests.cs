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
        var sut = CreateNamespaceAppService(
            namespaceRepository,
            new AuthorizationConfig { IsEnabled = true, AdminRoles = ["admin"] }
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
        var sut = CreateNamespaceAppService(
            namespaceRepository,
            new AuthorizationConfig { IsEnabled = true, AdminRoles = ["admin"] }
        );

        var created = await sut.CreateAsync(
            new Data.Models.Namespace { Name = "created-by-admin", PseudonymLength = 16 },
            UserWithRoles("admin"),
            CancellationToken.None
        );

        created.Name.Should().Be("created-by-admin");
    }

    [Fact]
    public async Task CreateAsync_WithFixedLengthMethodAndMismatchedLength_ShouldThrowArgumentOutOfRange()
    {
        // Uuid4 always produces a 36-character value - a namespace can't be created asking for
        // anything else, rather than only failing later at first pseudonym-creation time.
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var act = () =>
            sut.CreateAsync(
                new Data.Models.Namespace
                {
                    Name = "should-not-be-created",
                    PseudonymLength = 16,
                    PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.Uuid4,
                },
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task CreateAsync_WithFixedLengthMethodAndCorrectLength_ShouldSucceed()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var created = await sut.CreateAsync(
            new Data.Models.Namespace
            {
                Name = "uuid4-namespace",
                PseudonymLength = 36,
                PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.Uuid4,
            },
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        created.Name.Should().Be("uuid4-namespace");
    }

    [Fact]
    public async Task GetAllAsync_WithAuthorizationEnabled_ShouldOnlyReturnNamespacesUserCanRead()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(
            namespaceRepository,
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
        );

        var result = await sut.GetAllAsync(
            UserWithRoles("can-read-existing"),
            CancellationToken.None
        );

        result.Should().ContainSingle(n => n.Name == "existingNamespace");
        result.Should().NotContain(n => n.Name == "emptyNamespace");
    }

    [Fact]
    public async Task GetAsync_WithAuthorizationEnabledAndNoReadAccess_ShouldThrowForbidden()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(
            namespaceRepository,
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
        );

        var act = () =>
            sut.GetAsync(
                "existingNamespace",
                UserWithRoles("some-other-role"),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task GetAsync_WithAuthorizationEnabledAndReadAccess_ShouldReturnNamespace()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(
            namespaceRepository,
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
        );

        var result = await sut.GetAsync(
            "existingNamespace",
            UserWithRoles("can-read-existing"),
            CancellationToken.None
        );

        result.Name.Should().Be("existingNamespace");
    }

    [Fact]
    public async Task GetAsync_WithNonExistingNamespace_ShouldThrowNamespaceNotFoundException()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var act = () => sut.GetAsync("notExisting", new ClaimsPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<NamespaceNotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_WithAuthorizationEnabledAndNonAdminUser_ShouldThrowForbidden()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(
            namespaceRepository,
            new AuthorizationConfig { IsEnabled = true, AdminRoles = ["admin"] }
        );

        var act = () =>
            sut.DeleteAsync(
                "existingNamespace",
                UserWithRoles("some-other-role"),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ForbiddenException>();
        (await namespaceRepository.FindAsync("existingNamespace", CancellationToken.None))
            .Should()
            .NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_WithAuthorizationEnabledAndAdminUser_ShouldDeleteNamespace()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(
            namespaceRepository,
            new AuthorizationConfig { IsEnabled = true, AdminRoles = ["admin"] }
        );

        await sut.DeleteAsync("existingNamespace", UserWithRoles("admin"), CancellationToken.None);

        (await namespaceRepository.FindAsync("existingNamespace", CancellationToken.None))
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WithNonExistingNamespace_ShouldThrowNamespaceNotFoundException()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var act = () =>
            sut.DeleteAsync("notExisting", new ClaimsPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<NamespaceNotFoundException>();
    }
}
