using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Networks;

namespace Vfps.IntegrationTests;

public sealed class NetworkFixture : IAsyncLifetime
{
    public INetwork Network { get; } =
        new NetworkBuilder()
            .WithDriver(NetworkDriver.Bridge)
            .WithName(Guid.NewGuid().ToString("D"))
            .Build();

    public async ValueTask InitializeAsync()
    {
        await this.Network.CreateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await this.Network.DeleteAsync();
    }
}
