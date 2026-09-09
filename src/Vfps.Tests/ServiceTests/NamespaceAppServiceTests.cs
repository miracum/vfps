using System.Security.Claims;
using Vfps.Config;
using Vfps.Data.Models;

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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_WithBlankName_ShouldThrowArgumentException(string name)
    {
        // A nameless namespace can't be addressed in a URL and can't be named by a per-namespace
        // access grant, so it's rejected here rather than left in the database. The Blazor form's
        // Required attribute isn't enough on its own: a submit racing the form's post-create model
        // reset arrives with the name already cleared, and the gRPC API doesn't validate it at all.
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var act = () =>
            sut.CreateAsync(
                new Data.Models.Namespace { Name = name, PseudonymLength = 16 },
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>();
        (await namespaceRepository.GetAllAsync(CancellationToken.None))
            .Should()
            .NotContain(n => n.Name == name);
    }

    [Fact]
    public async Task CreateAsync_WithInvalidOriginalValueValidationRegex_ShouldThrowArgumentException()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var act = () =>
            sut.CreateAsync(
                new Data.Models.Namespace
                {
                    Name = "should-not-be-created",
                    PseudonymLength = 16,
                    OriginalValueValidationRegex = "(unterminated",
                },
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_WithValidOriginalValueValidationRegex_ShouldSucceed()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var created = await sut.CreateAsync(
            new Data.Models.Namespace
            {
                Name = "with-validation-regex",
                PseudonymLength = 16,
                OriginalValueValidationRegex = "^[0-9]+$",
            },
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        created.OriginalValueValidationRegex.Should().Be("^[0-9]+$");
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
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("existingNamespace", "can-read-existing", read: true)
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
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("existingNamespace", "can-read-existing", read: true)
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
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("existingNamespace", "can-read-existing", read: true)
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

    [Fact]
    public async Task CreateAsync_WithExistingParent_ShouldCreateChildNamespace()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var created = await sut.CreateAsync(
            new Data.Models.Namespace
            {
                Name = "child",
                PseudonymLength = 16,
                ParentName = "existingNamespace",
                ParentValidationMode = ParentValidationMode.EnsureExists,
            },
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        created.ParentName.Should().Be("existingNamespace");
        created.ParentValidationMode.Should().Be(ParentValidationMode.EnsureExists);
    }

    [Fact]
    public async Task CreateAsync_WithNonExistingParent_ShouldThrowNamespaceNotFoundException()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var act = () =>
            sut.CreateAsync(
                new Data.Models.Namespace
                {
                    Name = "orphan",
                    PseudonymLength = 16,
                    ParentName = "notExisting",
                },
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<NamespaceNotFoundException>();
    }

    [Fact]
    public async Task CreateAsync_WithSelfAsParent_ShouldThrowArgumentException()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var act = () =>
            sut.CreateAsync(
                new Data.Models.Namespace
                {
                    Name = "self-parenting",
                    PseudonymLength = 16,
                    ParentName = "self-parenting",
                },
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_WithValidationModeButNoParent_ShouldThrowArgumentException()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var act = () =>
            sut.CreateAsync(
                new Data.Models.Namespace
                {
                    Name = "validating-root",
                    PseudonymLength = 16,
                    ParentValidationMode = ParentValidationMode.EnsureExists,
                },
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DeleteAsync_WithChildNamespaces_ShouldThrowNamespaceHasChildrenException()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);
        await sut.CreateAsync(
            new Data.Models.Namespace
            {
                Name = "child-blocking-delete",
                PseudonymLength = 16,
                ParentName = "existingNamespace",
            },
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        var act = () =>
            sut.DeleteAsync("existingNamespace", new ClaimsPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<NamespaceHasChildrenException>();
        (await namespaceRepository.FindAsync("existingNamespace", CancellationToken.None))
            .Should()
            .NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_AfterChildrenAreDeleted_ShouldDeleteParent()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);
        await sut.CreateAsync(
            new Data.Models.Namespace
            {
                Name = "temporary-child",
                PseudonymLength = 16,
                ParentName = "emptyNamespace",
            },
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        await sut.DeleteAsync("temporary-child", new ClaimsPrincipal(), CancellationToken.None);
        await sut.DeleteAsync("emptyNamespace", new ClaimsPrincipal(), CancellationToken.None);

        (await namespaceRepository.FindAsync("emptyNamespace", CancellationToken.None))
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task ListChildrenAsync_ShouldReturnOnlyDirectChildren()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);
        foreach (
            var (name, parent) in new[]
            {
                ("level-1-a", "existingNamespace"),
                ("level-1-b", "existingNamespace"),
                ("level-2", "level-1-a"),
            }
        )
        {
            await sut.CreateAsync(
                new Data.Models.Namespace
                {
                    Name = name,
                    PseudonymLength = 16,
                    ParentName = parent,
                },
                new ClaimsPrincipal(),
                CancellationToken.None
            );
        }

        var children = await sut.ListChildrenAsync(
            "existingNamespace",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        // level-2 is a grandchild - this listing is deliberately non-recursive.
        children.Select(n => n.Name).Should().BeEquivalentTo(["level-1-a", "level-1-b"]);
    }

    [Fact]
    public async Task ListChildrenAsync_ShouldOmitChildrenTheCallerCannotRead()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var config = new AuthorizationConfig { IsEnabled = true };
        NamespaceAccessGrant[] grants =
        [
            Grants.ForRole("existingNamespace", "reader", read: true),
            Grants.ForRole("visible-child", "reader", read: true),
        ];
        var admin = CreateNamespaceAppService(
            namespaceRepository,
            new AuthorizationConfig { IsEnabled = true, AdminRoles = ["admin"] }
        );
        foreach (var name in new[] { "visible-child", "hidden-child" })
        {
            await admin.CreateAsync(
                new Data.Models.Namespace
                {
                    Name = name,
                    PseudonymLength = 16,
                    ParentName = "existingNamespace",
                },
                UserWithRoles("admin"),
                CancellationToken.None
            );
        }

        var sut = CreateNamespaceAppService(namespaceRepository, config, grants);
        var children = await sut.ListChildrenAsync(
            "existingNamespace",
            UserWithRoles("reader"),
            CancellationToken.None
        );

        children.Select(n => n.Name).Should().BeEquivalentTo(["visible-child"]);
    }

    [Fact]
    public async Task ListChildrenAsync_WithNonExistingNamespace_ShouldThrowNamespaceNotFoundException()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var act = () =>
            sut.ListChildrenAsync("notExisting", new ClaimsPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<NamespaceNotFoundException>();
    }
}
