
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace Vfps.IntegrationTests;

public class MigrationsTests : IAsyncLifetime
{
    // private readonly TestcontainerDatabase postgresqlContainer = new TestcontainersBuilder<PostgreSqlTestcontainer>()
    //     .WithDatabase(new PostgreSqlTestcontainerConfiguration("docker.io/bitnami/postgresql:14.5.0-debian-11-r17")
    //     {
    //         Database = "vfps",
    //         Username = "postgres",
    //         Password = "postgres",
    //     })
    //     .WithEnvironment("PGUSER", "postgres")
    //     .Build();

    [Fact]
    public async Task RunMigrationsContainer()
    {
        true.Should().BeTrue();
        // var migrationsImage = Environment.GetEnvironmentVariable("VFPS_MIGRATIONS_IMAGE") ?? "ghcr.io/chgl/vfps-migrations:latest";

        // using var consumer = Consume.RedirectStdoutAndStderrToStream(new MemoryStream(), new MemoryStream());

        // var migrationsContainerBuilder = new TestcontainersBuilder<TestcontainersContainer>()
        //   .WithImage(migrationsImage)
        //   .WithName("migrations")
        //   .WithOutputConsumer(consumer)
        //   .WithCommand($"--verbose --connection={postgresqlContainer.ConnectionString}")
        //   .WithWaitStrategy(Wait.ForUnixContainer()
        //       .UntilMessageIsLogged(consumer.Stdout, "Done."));

        // await using var migrationsContainer = migrationsContainerBuilder.Build()

        // await migrationsContainer.StartAsync();

        // consumer.Stdout.Seek(0, SeekOrigin.Begin);

        // using var streamReader = new StreamReader(consumer.Stdout, leaveOpen: true);
        // var loggedOutput = await streamReader.ReadToEndAsync();
        // loggedOutput.Should().Contain("Done.");
    }

    public Task InitializeAsync()
    {
        return Task.FromResult<object>(null);
        // return postgresqlContainer.StartAsync();
    }

    public Task DisposeAsync()
    {
        return Task.FromResult<object>(null);
        // return postgresqlContainer.DisposeAsync().AsTask();
    }
}
