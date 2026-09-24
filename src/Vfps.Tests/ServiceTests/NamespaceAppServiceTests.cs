using System.Security.Claims;
using FakeItEasy;
using Microsoft.Extensions.Caching.Memory;
using Vfps.Config;
using Vfps.Data.Models;
using Vfps.PseudonymGenerators;

namespace Vfps.Tests.ServiceTests;

public class NamespaceAppServiceTests : ServiceTestBase
{
    private static ClaimsPrincipal UserWithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim("roles", r))));

    [Fact]
    public async Task CreateAsync_WithAuthorizationEnabledAndNonAdminUser_ShouldThrowForbidden()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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

    /// <summary>
    /// A lookup with a VOPRF generator wired in, standing in for a deployment that has a VOPRF
    /// server configured. The generator itself is faked - what is under test is namespace
    /// creation's handling of the method, not the protocol.
    /// </summary>
    private static PseudonymizationMethodsLookup LookupWithVoprf(uint fixedLength = 86)
    {
        var generator = A.Fake<IValueDependentPseudonymGenerator>(options =>
            options.Implements<IHasFixedPseudonymLength>()
        );
        A.CallTo(() => ((IHasFixedPseudonymLength)generator).FixedPseudonymLength)
            .Returns(fixedLength);

        return new PseudonymizationMethodsLookup(generator);
    }

    [Fact]
    public async Task CreateAsync_WithVoprfAndNoConfiguredServer_ShouldThrowNotSupported()
    {
        // Nothing could mint a pseudonym in such a namespace, and the failure belongs to whoever
        // creates it rather than to whoever first tries to use it.
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var act = () =>
            sut.CreateAsync(
                new Data.Models.Namespace
                {
                    Name = "should-not-be-created",
                    PseudonymLength = 86,
                    PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.Voprf,
                },
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<PseudonymGenerationMethodNotSupportedException>();
    }

    [Fact]
    public async Task CreateAsync_WithVoprfAndMultiplePseudonyms_ShouldThrowArgumentException()
    {
        // The combination the whole value-dependent seam has to refuse: a VOPRF pseudonym is a
        // function of the original value, so there is no second distinct one to hand out. Allowing
        // it would mean either a later failure or the same value stored twice under different
        // sequence numbers.
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var sut = CreateNamespaceAppService(namespaceRepository, LookupWithVoprf());

        var act = () =>
            sut.CreateAsync(
                new Data.Models.Namespace
                {
                    Name = "should-not-be-created",
                    PseudonymLength = 86,
                    PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.Voprf,
                    AllowsMultiplePseudonyms = true,
                },
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_WithVoprfAndMismatchedLength_ShouldThrowArgumentOutOfRange()
    {
        // The VOPRF output length is a deployment-wide contract, not a per-namespace choice -
        // enforced through the same IHasFixedPseudonymLength path UUIDs use.
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var sut = CreateNamespaceAppService(namespaceRepository, LookupWithVoprf(fixedLength: 86));

        var act = () =>
            sut.CreateAsync(
                new Data.Models.Namespace
                {
                    Name = "should-not-be-created",
                    PseudonymLength = 32,
                    PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.Voprf,
                },
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task CreateAsync_WithVoprfConfiguredCorrectly_ShouldSucceed()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var sut = CreateNamespaceAppService(namespaceRepository, LookupWithVoprf());

        var created = await sut.CreateAsync(
            new Data.Models.Namespace
            {
                Name = "voprf-namespace",
                PseudonymLength = 86,
                PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.Voprf,
            },
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        created.Name.Should().Be("voprf-namespace");
        created.PseudonymGenerationMethod.Should().Be(Protos.PseudonymGenerationMethod.Voprf);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var act = () => sut.GetAsync("notExisting", new ClaimsPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<NamespaceNotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_WithAuthorizationEnabledAndNonAdminUser_ShouldThrowForbidden()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
    public async Task DeleteAsync_WithNamespaceCaching_ShouldEvictTheCachedNamespace()
    {
        // The eviction lives in CachingNamespaceRepository.DeleteAsync, so it only happens if the
        // app service deletes through the repository it was given rather than one of its own.
        var namespaceRepository = new CachingNamespaceRepository(
            ContextFactory,
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 2048 }),
            new CacheConfig()
        );
        var sut = CreateNamespaceAppService(namespaceRepository);
        (await namespaceRepository.FindAsync("existingNamespace", CancellationToken.None))
            .Should()
            .NotBeNull();

        await sut.DeleteAsync("existingNamespace", new ClaimsPrincipal(), CancellationToken.None);

        (await namespaceRepository.FindAsync("existingNamespace", CancellationToken.None))
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WithNonExistingNamespace_ShouldThrowNamespaceNotFoundException()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var act = () =>
            sut.DeleteAsync("notExisting", new ClaimsPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<NamespaceNotFoundException>();
    }

    [Fact]
    public async Task CreateAsync_WithExistingParent_ShouldCreateChildNamespace()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var sut = CreateNamespaceAppService(namespaceRepository);

        var act = () =>
            sut.ListChildrenAsync("notExisting", new ClaimsPrincipal(), CancellationToken.None);

        await act.Should().ThrowAsync<NamespaceNotFoundException>();
    }
}
