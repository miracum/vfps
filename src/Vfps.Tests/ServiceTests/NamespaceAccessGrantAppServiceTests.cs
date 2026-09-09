using System.Security.Claims;
using Vfps.Config;
using Vfps.Data.Models;

namespace Vfps.Tests.ServiceTests;

public class NamespaceAccessGrantAppServiceTests : ServiceTestBase
{
    private const string ExistingNamespace = "existingNamespace";

    private static ClaimsPrincipal UserWithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim("roles", r))));

    private static readonly AuthorizationConfig EnabledConfig = new()
    {
        IsEnabled = true,
        AdminRoles = ["admin"],
    };

    private (
        NamespaceAccessGrantAppService Sut,
        NamespaceAccessGrantRepository Repository,
        StaticNamespaceAccessGrantCache Cache
    ) CreateSut()
    {
        var grantRepository = new NamespaceAccessGrantRepository(InMemoryPseudonymContext);
        var cache = new StaticNamespaceAccessGrantCache();
        var sut = new NamespaceAccessGrantAppService(
            grantRepository,
            new NamespaceRepository(InMemoryPseudonymContext),
            CreatePermissionChecker(EnabledConfig),
            cache
        );

        return (sut, grantRepository, cache);
    }

    private static ClaimsPrincipal Admin => UserWithRoles("admin");

    [Fact]
    public async Task EveryMethod_AsNonAdmin_ShouldThrowForbidden()
    {
        var (sut, _, _) = CreateSut();
        var nonAdmin = UserWithRoles("some-role");

        await FluentActions
            .Awaiting(() => sut.GetAllAsync(nonAdmin, CancellationToken.None))
            .Should()
            .ThrowAsync<ForbiddenException>();

        await FluentActions
            .Awaiting(() =>
                sut.CreateAsync(
                    ExistingNamespace,
                    GranteeType.Role,
                    "reader",
                    canRead: true,
                    canWrite: false,
                    canReverseLookup: false,
                    nonAdmin,
                    CancellationToken.None
                )
            )
            .Should()
            .ThrowAsync<ForbiddenException>();

        await FluentActions
            .Awaiting(() =>
                sut.UpdateAsync(
                    Guid.NewGuid(),
                    canRead: true,
                    canWrite: false,
                    canReverseLookup: false,
                    nonAdmin,
                    CancellationToken.None
                )
            )
            .Should()
            .ThrowAsync<ForbiddenException>();

        await FluentActions
            .Awaiting(() => sut.DeleteAsync(Guid.NewGuid(), nonAdmin, CancellationToken.None))
            .Should()
            .ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task CreateAsync_ShouldStoreTheGrantAndInvalidateTheCache()
    {
        var (sut, repository, cache) = CreateSut();

        var grant = await sut.CreateAsync(
            ExistingNamespace,
            GranteeType.Role,
            "vfps-ns-read",
            canRead: true,
            canWrite: false,
            canReverseLookup: true,
            Admin,
            CancellationToken.None
        );

        grant.NamespaceName.Should().Be(ExistingNamespace);
        grant.Grantee.Should().Be("vfps-ns-read");
        grant.CanRead.Should().BeTrue();
        grant.CanWrite.Should().BeFalse();
        grant.CanReverseLookup.Should().BeTrue();

        var stored = await repository.GetAllAsync(CancellationToken.None);
        stored.Should().ContainSingle().Which.Id.Should().Be(grant.Id);
        cache.InvalidateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_WithoutANamespace_ShouldCreateAGrantForEveryNamespace()
    {
        var (sut, _, _) = CreateSut();

        var grant = await sut.CreateAsync(
            null,
            GranteeType.Role,
            "global-reader",
            canRead: true,
            canWrite: false,
            canReverseLookup: false,
            Admin,
            CancellationToken.None
        );

        grant.NamespaceName.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ForANamespaceThatDoesNotExist_ShouldThrowNamespaceNotFound()
    {
        var (sut, _, _) = CreateSut();

        var act = () =>
            sut.CreateAsync(
                "notExisting",
                GranteeType.Role,
                "reader",
                canRead: true,
                canWrite: false,
                canReverseLookup: false,
                Admin,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<NamespaceNotFoundException>();
    }

    [Fact]
    public async Task CreateAsync_GrantingNothingAtAll_ShouldThrow()
    {
        var (sut, _, _) = CreateSut();

        var act = () =>
            sut.CreateAsync(
                ExistingNamespace,
                GranteeType.Role,
                "reader",
                canRead: false,
                canWrite: false,
                canReverseLookup: false,
                Admin,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_WithABlankGrantee_ShouldThrow(string grantee)
    {
        var (sut, _, _) = CreateSut();

        var act = () =>
            sut.CreateAsync(
                ExistingNamespace,
                GranteeType.Role,
                grantee,
                canRead: true,
                canWrite: false,
                canReverseLookup: false,
                Admin,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_WithAMalformedEmail_ShouldThrow()
    {
        var (sut, _, _) = CreateSut();

        var act = () =>
            sut.CreateAsync(
                ExistingNamespace,
                GranteeType.Email,
                "not-an-email",
                canRead: true,
                canWrite: false,
                canReverseLookup: false,
                Admin,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_ShouldLowerCaseAnEmailGranteeButLeaveARoleAsTyped()
    {
        var (sut, _, _) = CreateSut();

        var email = await sut.CreateAsync(
            ExistingNamespace,
            GranteeType.Email,
            "  User@Example.ORG ",
            canRead: true,
            canWrite: false,
            canReverseLookup: false,
            Admin,
            CancellationToken.None
        );
        var role = await sut.CreateAsync(
            ExistingNamespace,
            GranteeType.Role,
            "  Vfps-Reader ",
            canRead: true,
            canWrite: false,
            canReverseLookup: false,
            Admin,
            CancellationToken.None
        );

        email.Grantee.Should().Be("user@example.org");
        role.Grantee.Should().Be("Vfps-Reader");
    }

    [Fact]
    public async Task CreateAsync_ForAGranteeThatAlreadyHasAGrantOnTheSameNamespace_ShouldThrow()
    {
        var (sut, _, _) = CreateSut();
        await sut.CreateAsync(
            ExistingNamespace,
            GranteeType.Role,
            "reader",
            canRead: true,
            canWrite: false,
            canReverseLookup: false,
            Admin,
            CancellationToken.None
        );

        var act = () =>
            sut.CreateAsync(
                ExistingNamespace,
                GranteeType.Role,
                "reader",
                canRead: false,
                canWrite: true,
                canReverseLookup: false,
                Admin,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<NamespaceAccessGrantAlreadyExistsException>();
    }

    [Fact]
    public async Task CreateAsync_ForTheSameGranteeOnADifferentScope_ShouldBeAllowed()
    {
        var (sut, repository, _) = CreateSut();

        await sut.CreateAsync(
            ExistingNamespace,
            GranteeType.Role,
            "reader",
            canRead: true,
            canWrite: false,
            canReverseLookup: false,
            Admin,
            CancellationToken.None
        );
        await sut.CreateAsync(
            null,
            GranteeType.Role,
            "reader",
            canRead: true,
            canWrite: false,
            canReverseLookup: false,
            Admin,
            CancellationToken.None
        );
        // Same name, different grantee type - a role named like an address is still not that user.
        await sut.CreateAsync(
            ExistingNamespace,
            GranteeType.Email,
            "reader@example.org",
            canRead: true,
            canWrite: false,
            canReverseLookup: false,
            Admin,
            CancellationToken.None
        );

        (await repository.GetAllAsync(CancellationToken.None)).Should().HaveCount(3);
    }

    [Fact]
    public async Task UpdateAsync_ShouldReplaceThePermissionsAndInvalidateTheCache()
    {
        var (sut, repository, cache) = CreateSut();
        var grant = await sut.CreateAsync(
            ExistingNamespace,
            GranteeType.Role,
            "reader",
            canRead: true,
            canWrite: false,
            canReverseLookup: false,
            Admin,
            CancellationToken.None
        );

        await sut.UpdateAsync(
            grant.Id,
            canRead: false,
            canWrite: true,
            canReverseLookup: true,
            Admin,
            CancellationToken.None
        );

        var stored = await repository.FindAsync(grant.Id, CancellationToken.None);
        stored.Should().NotBeNull();
        stored!.CanRead.Should().BeFalse();
        stored.CanWrite.Should().BeTrue();
        stored.CanReverseLookup.Should().BeTrue();
        cache.InvalidateCallCount.Should().Be(2);
    }

    [Fact]
    public async Task UpdateAsync_ClearingEveryPermission_ShouldBeAllowed()
    {
        // Unlike CreateAsync: the UI saves each checkbox as it's toggled, so unchecking the last
        // one has to persist rather than error - the row is then a visible no-op to delete.
        var (sut, repository, _) = CreateSut();
        var grant = await sut.CreateAsync(
            ExistingNamespace,
            GranteeType.Role,
            "reader",
            canRead: true,
            canWrite: false,
            canReverseLookup: false,
            Admin,
            CancellationToken.None
        );

        await sut.UpdateAsync(
            grant.Id,
            canRead: false,
            canWrite: false,
            canReverseLookup: false,
            Admin,
            CancellationToken.None
        );

        var stored = await repository.FindAsync(grant.Id, CancellationToken.None);
        stored!.CanRead.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_ForAGrantThatDoesNotExist_ShouldThrow()
    {
        var (sut, _, _) = CreateSut();

        var act = () =>
            sut.UpdateAsync(
                Guid.NewGuid(),
                canRead: true,
                canWrite: false,
                canReverseLookup: false,
                Admin,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<NamespaceAccessGrantNotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveTheGrantAndInvalidateTheCache()
    {
        var (sut, repository, cache) = CreateSut();
        var grant = await sut.CreateAsync(
            ExistingNamespace,
            GranteeType.Role,
            "reader",
            canRead: true,
            canWrite: false,
            canReverseLookup: false,
            Admin,
            CancellationToken.None
        );

        await sut.DeleteAsync(grant.Id, Admin, CancellationToken.None);

        (await repository.GetAllAsync(CancellationToken.None)).Should().BeEmpty();
        cache.InvalidateCallCount.Should().Be(2);
    }

    [Fact]
    public async Task DeleteAsync_ForAGrantThatDoesNotExist_ShouldThrow()
    {
        var (sut, _, _) = CreateSut();

        var act = () => sut.DeleteAsync(Guid.NewGuid(), Admin, CancellationToken.None);

        await act.Should().ThrowAsync<NamespaceAccessGrantNotFoundException>();
    }
}
