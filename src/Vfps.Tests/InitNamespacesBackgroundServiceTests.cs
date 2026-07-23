using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        var services = new ServiceCollection();
        services.AddSingleton<INamespaceRepository>(
            new NamespaceRepository(InMemoryPseudonymContext)
        );
        var serviceProvider = services.BuildServiceProvider();

        var configuration = BuildConfiguration("existingNamespace", "newlyConfiguredNamespace");
        var sut = new InitNamespacesBackgroundService(
            serviceProvider,
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
}
