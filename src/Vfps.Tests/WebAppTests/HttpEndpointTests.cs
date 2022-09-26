using Vfps.Data;

namespace Vfps.Tests.WebAppTests;

public class HttpEndpointTests : IClassFixture<IntegrationTestFactory<Program, PseudonymContext>>
{
    private readonly IntegrationTestFactory<Program, PseudonymContext> _factory;

    public HttpEndpointTests(IntegrationTestFactory<Program, PseudonymContext> factory) => _factory = factory;

    [Theory]
    [InlineData("/readyz")]
    [InlineData("/livez")]
    [InlineData("/healthz")]
    [InlineData("/metrics")]
    [InlineData("/")]
    public async Task HealthMetricsAndRootEndpoints_ShouldReturnSuccess(string endpoint)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(endpoint);
        response.EnsureSuccessStatusCode();
    }
}
