
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Xunit.Abstractions;

namespace Vfps.IntegrationTests;

public class MigrationsTests : IAsyncLifetime, IClassFixture<NetworkFixture>
{
    private readonly ITestOutputHelper output;

    private readonly TestcontainerDatabase postgresqlContainer;

    private readonly NetworkFixture networkFixture;

    private readonly string connectionString;

    private readonly string migrationsImage;

    public MigrationsTests(ITestOutputHelper output, NetworkFixture networkFixture)
    {
        this.output = output;
        this.networkFixture = networkFixture;

        postgresqlContainer = new TestcontainersBuilder<PostgreSqlTestcontainer>()
            .WithDatabase(new PostgreSqlTestcontainerConfiguration("docker.io/bitnami/postgresql:14.5.0-debian-11-r17")
            {
                Database = "vfps",
                Username = "postgres",
                Password = "postgres",
            })
            .WithName("postgres")
            .WithHostname("postgres")
            .WithEnvironment("PGUSER", "postgres")
            .WithNetwork(networkFixture.Network.Id, networkFixture.Network.Name)
            .Build();

        this.connectionString = "Server=postgres;Port=5432;Database=vfps;User Id=postgres;Password=postgres;";

        var migrationsImageTag = Environment.GetEnvironmentVariable("VFPS_MIGRATIONS_IMAGE_TAG") ?? "latest";
        this.migrationsImage = $"ghcr.io/chgl/vfps-migrations:{migrationsImageTag}";
    }

    [Fact]
    public async Task RunMigrationsContainer_WithCorrectConnectionString_ShouldSucceed()
    {
        using var consumer = Consume.RedirectStdoutAndStderrToStream(new MemoryStream(), new MemoryStream());

        var migrationsContainerBuilder = new TestcontainersBuilder<TestcontainersContainer>()
            .WithImage(migrationsImage)
            .WithName("migrations")
            .WithOutputConsumer(consumer)
            .WithNetwork(networkFixture.Network.Id, networkFixture.Network.Name)
            .WithCommand("--verbose", $"--connection={connectionString}");

        await using var migrationsContainer = migrationsContainerBuilder.Build();

        await migrationsContainer.StartAsync();

        var exitCode = await migrationsContainer.GetExitCode();

        consumer.Stdout.Seek(0, SeekOrigin.Begin);
        using var stdoutReader = new StreamReader(consumer.Stdout);
        var stdout = stdoutReader.ReadToEnd();
        output.WriteLine(stdout);

        exitCode.Should().Be(0);
        stdout.Should().Contain("Done.");
    }

    [Fact]
    public async Task RunMigrationsContainer_WithWrongConnectionString_ShouldFail()
    {
        using var consumer = Consume.RedirectStdoutAndStderrToStream(new MemoryStream(), new MemoryStream());

        var migrationsContainerBuilder = new TestcontainersBuilder<TestcontainersContainer>()
            .WithImage(migrationsImage)
            .WithName("migrations")
            .WithOutputConsumer(consumer)
            .WithNetwork(networkFixture.Network.Id, networkFixture.Network.Name)
            .WithCommand("--verbose", "--connection=Server=not-postgres;Port=5432;Database=vfps;User Id=postgres;Password=postgres;");

        await using var migrationsContainer = migrationsContainerBuilder.Build();

        await migrationsContainer.StartAsync();

        var exitCode = await migrationsContainer.GetExitCode();

        consumer.Stdout.Seek(0, SeekOrigin.Begin);
        using var stdoutReader = new StreamReader(consumer.Stdout);
        var stdout = stdoutReader.ReadToEnd();
        output.WriteLine(stdout);

        exitCode.Should().Be(1);
    }

    public Task InitializeAsync()
    {
        return postgresqlContainer.StartAsync();
    }

    public Task DisposeAsync()
    {
        return postgresqlContainer.DisposeAsync().AsTask();
    }
}
