using EntityFramework.Exceptions.Common;
using Microsoft.EntityFrameworkCore;
using Vfps.Data.Models;

namespace Vfps.Tests.DataTests;

public class ProjectRepositoryTests : ServiceTests.ServiceTestBase
{
    private ProjectRepository CreateSut() => new(ContextFactory);

    private static Data.Models.Project NewProject(string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new Data.Models.Project
        {
            Name = name,
            Description = $"{name} description",
            CreatedAt = now,
            LastUpdatedAt = now,
        };
    }

    [Fact]
    public async Task CreateAsync_WithAnExistingName_ShouldThrowUniqueConstraintException()
    {
        var sut = CreateSut();
        await sut.CreateAsync(NewProject("study"), TestContext.Current.CancellationToken);

        var act = () => sut.CreateAsync(NewProject("study"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<UniqueConstraintException>();
    }

    [Fact]
    public async Task AddNamespaceAsync_ShouldBeIdempotent()
    {
        var sut = CreateSut();
        await sut.CreateAsync(NewProject("study"), TestContext.Current.CancellationToken);

        await sut.AddNamespaceAsync(
            "study",
            "existingNamespace",
            TestContext.Current.CancellationToken
        );
        await sut.AddNamespaceAsync(
            "study",
            "existingNamespace",
            TestContext.Current.CancellationToken
        );

        var found = await sut.FindAsync("study", TestContext.Current.CancellationToken);
        found!
            .Memberships.Should()
            .ContainSingle()
            .Which.NamespaceName.Should()
            .Be("existingNamespace");
    }

    [Fact]
    public async Task AddNamespaceAsync_WithAnUnknownNamespace_ShouldThrowReferenceConstraintException()
    {
        var sut = CreateSut();
        await sut.CreateAsync(NewProject("study"), TestContext.Current.CancellationToken);

        var act = () =>
            sut.AddNamespaceAsync(
                "study",
                "no-such-namespace",
                TestContext.Current.CancellationToken
            );

        await act.Should().ThrowAsync<ReferenceConstraintException>();
    }

    [Fact]
    public async Task ANamespace_ShouldBeAbleToBelongToSeveralProjects()
    {
        var sut = CreateSut();
        await sut.CreateAsync(NewProject("study-a"), TestContext.Current.CancellationToken);
        await sut.CreateAsync(NewProject("study-b"), TestContext.Current.CancellationToken);

        await sut.AddNamespaceAsync(
            "study-a",
            "existingNamespace",
            TestContext.Current.CancellationToken
        );
        await sut.AddNamespaceAsync(
            "study-b",
            "existingNamespace",
            TestContext.Current.CancellationToken
        );

        var all = await sut.GetAllAsync(TestContext.Current.CancellationToken);
        all.Should().HaveCount(2);
        all.Should()
            .OnlyContain(p => p.Memberships.Any(m => m.NamespaceName == "existingNamespace"));
    }

    [Fact]
    public async Task DeletingAProject_ShouldRemoveItsMembershipsButNotItsNamespaces()
    {
        var sut = CreateSut();
        await sut.CreateAsync(NewProject("study"), TestContext.Current.CancellationToken);
        await sut.AddNamespaceAsync(
            "study",
            "existingNamespace",
            TestContext.Current.CancellationToken
        );

        await sut.DeleteAsync("study", TestContext.Current.CancellationToken);

        (await sut.FindAsync("study", TestContext.Current.CancellationToken)).Should().BeNull();
        (
            await InMemoryPseudonymContext.ProjectNamespaces.CountAsync(
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(0);
        (
            await InMemoryPseudonymContext.Namespaces.AnyAsync(
                n => n.Name == "existingNamespace",
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .BeTrue();
        // ...and the pseudonym in it, which a cascade in the wrong direction would have taken.
        (
            await InMemoryPseudonymContext.Pseudonyms.CountAsync(
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task DeletingANamespace_ShouldRemoveItsMembershipsButNotItsProjects()
    {
        var sut = CreateSut();
        await sut.CreateAsync(NewProject("study"), TestContext.Current.CancellationToken);
        await sut.AddNamespaceAsync(
            "study",
            "emptyNamespace",
            TestContext.Current.CancellationToken
        );

        await new NamespaceRepository(ContextFactory).DeleteAsync(
            "emptyNamespace",
            TestContext.Current.CancellationToken
        );

        var found = await sut.FindAsync("study", TestContext.Current.CancellationToken);
        found.Should().NotBeNull();
        found!.Memberships.Should().BeEmpty();
    }

    [Fact]
    public async Task SetProjectsOfNamespaceAsync_ShouldReplaceTheSet()
    {
        var sut = CreateSut();
        foreach (var name in new[] { "a", "b", "c" })
        {
            await sut.CreateAsync(NewProject(name), TestContext.Current.CancellationToken);
        }

        await sut.SetProjectsOfNamespaceAsync(
            "existingNamespace",
            ["a", "b"],
            TestContext.Current.CancellationToken
        );
        await sut.SetProjectsOfNamespaceAsync(
            "existingNamespace",
            ["b", "c"],
            TestContext.Current.CancellationToken
        );

        var memberOf = (await sut.GetAllAsync(TestContext.Current.CancellationToken))
            .Where(p => p.Memberships.Any(m => m.NamespaceName == "existingNamespace"))
            .Select(p => p.Name);
        memberOf.Should().BeEquivalentTo(["b", "c"]);
    }

    [Fact]
    public async Task SetProjectsOfNamespaceAsync_WithAnUnknownProject_ShouldChangeNothing()
    {
        var sut = CreateSut();
        await sut.CreateAsync(NewProject("a"), TestContext.Current.CancellationToken);
        await sut.SetProjectsOfNamespaceAsync(
            "existingNamespace",
            ["a"],
            TestContext.Current.CancellationToken
        );

        var act = () =>
            sut.SetProjectsOfNamespaceAsync(
                "existingNamespace",
                ["no-such-project"],
                TestContext.Current.CancellationToken
            );
        await act.Should().ThrowAsync<ReferenceConstraintException>();

        // One save, so the removal of "a" went down with the failed insert.
        var found = await sut.FindAsync("a", TestContext.Current.CancellationToken);
        found!.Memberships.Should().ContainSingle();
    }
}
