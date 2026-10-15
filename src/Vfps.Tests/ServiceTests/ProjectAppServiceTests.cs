using System.Security.Claims;
using Vfps.Config;
using Vfps.Data.Models;

namespace Vfps.Tests.ServiceTests;

public class ProjectAppServiceTests : ServiceTestBase
{
    private static readonly AuthorizationConfig AuthorizationOn = new()
    {
        IsEnabled = true,
        AdminRoles = ["admin"],
    };

    private static ClaimsPrincipal UserWithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim("roles", r))));

    private static readonly ClaimsPrincipal Admin = UserWithRoles("admin");

    private static Data.Models.Project NewProject(string name, string? description = null) =>
        new() { Name = name, Description = description };

    [Fact]
    public async Task CreateAsync_ShouldStoreAnEmptyProject()
    {
        var sut = CreateProjectAppService();

        var created = await sut.CreateAsync(
            NewProject("study", "a study"),
            new ClaimsPrincipal(),
            TestContext.Current.CancellationToken
        );

        created.Name.Should().Be("study");
        created.Description.Should().Be("a study");
        created.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        created.Memberships.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WithABlankName_ShouldThrowArgumentException()
    {
        var sut = CreateProjectAppService();

        var act = () =>
            sut.CreateAsync(
                NewProject(" "),
                new ClaimsPrincipal(),
                TestContext.Current.CancellationToken
            );

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_WithAnExistingName_ShouldThrowAlreadyExists()
    {
        var sut = CreateProjectAppService();
        await sut.CreateAsync(
            NewProject("study"),
            new ClaimsPrincipal(),
            TestContext.Current.CancellationToken
        );

        var act = () =>
            sut.CreateAsync(
                NewProject("study"),
                new ClaimsPrincipal(),
                TestContext.Current.CancellationToken
            );

        await act.Should().ThrowAsync<ProjectAlreadyExistsException>();
    }

    [Fact]
    public async Task EveryWrite_WithAuthorizationEnabledAndNonAdminUser_ShouldThrowForbidden()
    {
        var sut = CreateProjectAppService(
            AuthorizationOn,
            Grants.ForRole(null, "everything", read: true, write: true, reverseLookup: true)
        );
        var user = UserWithRoles("everything");
        var ct = TestContext.Current.CancellationToken;

        // Not even a write grant on every namespace is enough: a project is not a namespace.
        var writes = new Func<Task>[]
        {
            () => sut.CreateAsync(NewProject("new"), user, ct),
            () => sut.UpdateAsync("study", "changed", user, ct),
            () => sut.DeleteAsync("study", user, ct),
            () => sut.AddNamespaceAsync("study", "existingNamespace", user, ct),
            () => sut.RemoveNamespaceAsync("study", "existingNamespace", user, ct),
            () => sut.SetProjectsOfNamespaceAsync("existingNamespace", ["study"], user, ct),
        };

        foreach (var write in writes)
        {
            await write.Should().ThrowAsync<ForbiddenException>();
        }
    }

    [Fact]
    public async Task AddNamespaceAsync_ShouldPutTheNamespaceIntoTheProject()
    {
        var sut = CreateProjectAppService();
        await sut.CreateAsync(NewProject("study"), Admin, TestContext.Current.CancellationToken);

        await sut.AddNamespaceAsync(
            "study",
            "existingNamespace",
            Admin,
            TestContext.Current.CancellationToken
        );

        var project = await sut.GetAsync("study", Admin, TestContext.Current.CancellationToken);
        project.Memberships.Select(m => m.NamespaceName).Should().Equal("existingNamespace");
    }

    [Fact]
    public async Task AddNamespaceAsync_WithAnUnknownNamespaceOrProject_ShouldThrowNotFound()
    {
        var sut = CreateProjectAppService();
        await sut.CreateAsync(NewProject("study"), Admin, TestContext.Current.CancellationToken);

        var unknownNamespace = () =>
            sut.AddNamespaceAsync(
                "study",
                "no-such-namespace",
                Admin,
                TestContext.Current.CancellationToken
            );
        var unknownProject = () =>
            sut.AddNamespaceAsync(
                "no-such-project",
                "existingNamespace",
                Admin,
                TestContext.Current.CancellationToken
            );

        await unknownNamespace.Should().ThrowAsync<NamespaceNotFoundException>();
        await unknownProject.Should().ThrowAsync<ProjectNotFoundException>();
    }

    [Fact]
    public async Task RemoveNamespaceAsync_ShouldTakeTheNamespaceOutOfTheProjectOnly()
    {
        var sut = CreateProjectAppService();
        await sut.CreateAsync(NewProject("study"), Admin, TestContext.Current.CancellationToken);
        await sut.AddNamespaceAsync(
            "study",
            "existingNamespace",
            Admin,
            TestContext.Current.CancellationToken
        );

        await sut.RemoveNamespaceAsync(
            "study",
            "existingNamespace",
            Admin,
            TestContext.Current.CancellationToken
        );

        (await sut.GetAsync("study", Admin, TestContext.Current.CancellationToken))
            .Memberships.Should()
            .BeEmpty();
        (
            await new NamespaceRepository(ContextFactory).FindAsync(
                "existingNamespace",
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .NotBeNull();
    }

    [Fact]
    public async Task SetProjectsOfNamespaceAsync_WithAnUnknownProject_ShouldThrowNotFoundAndChangeNothing()
    {
        var sut = CreateProjectAppService();
        await sut.CreateAsync(NewProject("study"), Admin, TestContext.Current.CancellationToken);

        var act = () =>
            sut.SetProjectsOfNamespaceAsync(
                "existingNamespace",
                ["study", "no-such-project"],
                Admin,
                TestContext.Current.CancellationToken
            );

        (await act.Should().ThrowAsync<ProjectNotFoundException>())
            .Which.ProjectName.Should()
            .Be("no-such-project");
        (await sut.GetAsync("study", Admin, TestContext.Current.CancellationToken))
            .Memberships.Should()
            .BeEmpty();
    }

    [Fact]
    public async Task GetAllAsync_ForANonAdmin_ShouldOnlyShowReadableNamespacesAndProjectsHavingOne()
    {
        var admin = CreateProjectAppService(AuthorizationOn);
        foreach (var name in new[] { "shared", "other", "empty" })
        {
            await admin.CreateAsync(NewProject(name), Admin, TestContext.Current.CancellationToken);
        }

        foreach (
            var (project, @namespace) in new[]
            {
                ("shared", "existingNamespace"),
                ("shared", "emptyNamespace"),
                ("other", "emptyNamespace"),
            }
        )
        {
            await admin.AddNamespaceAsync(
                project,
                @namespace,
                Admin,
                TestContext.Current.CancellationToken
            );
        }

        var sut = CreateProjectAppService(
            AuthorizationOn,
            Grants.ForRole("existingNamespace", "reader", read: true)
        );

        var visible = await sut.GetAllAsync(
            UserWithRoles("reader"),
            TestContext.Current.CancellationToken
        );

        // "other" only holds a namespace the caller can't read, and "empty" holds none at all.
        visible.Select(p => p.Name).Should().Equal("shared");
        visible
            .Single()
            .Memberships.Select(m => m.NamespaceName)
            .Should()
            .Equal("existingNamespace");
    }

    [Fact]
    public async Task GetAllAsync_ForAnAdmin_ShouldShowEveryProjectIncludingEmptyOnes()
    {
        var sut = CreateProjectAppService(AuthorizationOn);
        await sut.CreateAsync(NewProject("empty"), Admin, TestContext.Current.CancellationToken);

        var visible = await sut.GetAllAsync(Admin, TestContext.Current.CancellationToken);

        visible.Select(p => p.Name).Should().Equal("empty");
    }

    [Fact]
    public async Task GetAsync_ForAProjectWithNoReadableNamespace_ShouldThrowForbidden()
    {
        var admin = CreateProjectAppService(AuthorizationOn);
        await admin.CreateAsync(NewProject("study"), Admin, TestContext.Current.CancellationToken);
        await admin.AddNamespaceAsync(
            "study",
            "emptyNamespace",
            Admin,
            TestContext.Current.CancellationToken
        );
        var sut = CreateProjectAppService(
            AuthorizationOn,
            Grants.ForRole("existingNamespace", "reader", read: true)
        );

        var act = () =>
            sut.GetAsync("study", UserWithRoles("reader"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task UpdateAsync_ShouldReplaceTheDescription()
    {
        var sut = CreateProjectAppService();
        await sut.CreateAsync(
            NewProject("study", "old"),
            Admin,
            TestContext.Current.CancellationToken
        );

        var updated = await sut.UpdateAsync(
            "study",
            null,
            Admin,
            TestContext.Current.CancellationToken
        );

        updated.Description.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WithAnUnknownProject_ShouldThrowNotFound()
    {
        var sut = CreateProjectAppService();

        var act = () =>
            sut.DeleteAsync("no-such-project", Admin, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ProjectNotFoundException>();
    }
}
