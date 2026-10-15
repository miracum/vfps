using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Vfps.Tests.ServiceTests;

namespace Vfps.Tests.InitNamespacesBackgroundServiceTests;

public class InitNamespacesBackgroundServiceTests : ServiceTestBase
{
    private static IConfiguration BuildConfiguration(params string[] namespaceNames)
    {
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < namespaceNames.Length; i++)
        {
            settings[$"Init:v1:Namespaces:{i}:Name"] = namespaceNames[i];
            settings[$"Init:v1:Namespaces:{i}:PseudonymLength"] = "16";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public async Task ExecuteAsync_WithAnEarlierNamespaceAlreadyExisting_ShouldStillCreateTheRest()
    {
        // Regression test: the loop used to `return` (not `continue`) upon finding an
        // already-existing namespace, silently aborting initialization of every namespace
        // configured after it - "existingNamespace" here is seeded by ServiceTestBase itself.
        var configuration = BuildConfiguration("existingNamespace", "newlyConfiguredNamespace");
        var sut = new InitNamespacesBackgroundService(
            new NamespaceRepository(ContextFactory),
            new ProjectRepository(ContextFactory),
            configuration,
            NullLogger<InitNamespacesBackgroundService>.Instance
        );

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await sut.ExecuteTask!;

        var created = await InMemoryPseudonymContext
            .Namespaces.AsNoTracking()
            .FirstOrDefaultAsync(
                n => n.Name == "newlyConfiguredNamespace",
                TestContext.Current.CancellationToken
            );
        created.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WithConfiguredProjects_ShouldCreateThemAndTheirMemberships()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Init:v1:Namespaces:0:Name"] = "patient-ids",
                    ["Init:v1:Namespaces:0:PseudonymLength"] = "16",
                    ["Init:v1:Namespaces:1:Name"] = "encounter-ids",
                    ["Init:v1:Namespaces:1:PseudonymLength"] = "16",
                    ["Init:v1:Projects:0:Name"] = "study",
                    ["Init:v1:Projects:0:Description"] = "a study",
                    ["Init:v1:Projects:0:Namespaces:0"] = "patient-ids",
                    ["Init:v1:Projects:0:Namespaces:1"] = "encounter-ids",
                    ["Init:v1:Projects:0:Namespaces:2"] = "no-such-namespace",
                    // A namespace may be in several projects.
                    ["Init:v1:Projects:1:Name"] = "registry",
                    ["Init:v1:Projects:1:Namespaces:0"] = "patient-ids",
                }
            )
            .Build();
        var sut = new InitNamespacesBackgroundService(
            new NamespaceRepository(ContextFactory),
            new ProjectRepository(ContextFactory),
            configuration,
            NullLogger<InitNamespacesBackgroundService>.Instance
        );

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await sut.ExecuteTask!;

        var projects = await new ProjectRepository(ContextFactory).GetAllAsync(
            TestContext.Current.CancellationToken
        );
        projects.Should().HaveCount(2);
        var study = projects.Single(p => p.Name == "study");
        study.Description.Should().Be("a study");
        // The unknown namespace was skipped without taking the rest down with it.
        study
            .Memberships.Select(m => m.NamespaceName)
            .Should()
            .BeEquivalentTo(["patient-ids", "encounter-ids"]);
        projects
            .Single(p => p.Name == "registry")
            .Memberships.Select(m => m.NamespaceName)
            .Should()
            .Equal("patient-ids");
    }
}
