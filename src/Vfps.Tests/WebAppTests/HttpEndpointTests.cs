using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Microsoft.Extensions.DependencyInjection;
using Vfps.Data;
using Vfps.Protos;
using Task = System.Threading.Tasks.Task;

namespace Vfps.Tests.WebAppTests;

public class HttpEndpointTests : IClassFixture<IntegrationTestFactory<Program, PseudonymContext>>
{
    private readonly IntegrationTestFactory<Program, PseudonymContext> factory;

    public HttpEndpointTests(IntegrationTestFactory<Program, PseudonymContext> factory)
    {
        this.factory = factory;
    }

    [Theory]
    [InlineData("/readyz")]
    [InlineData("/livez")]
    [InlineData("/healthz")]
    [InlineData("/")]
    public async Task HealthMetricsAndRootEndpoints_ShouldReturnSuccess(string endpoint)
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync(endpoint);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task CreatePseudonym_ShouldSucceed()
    {
        var db = factory.Services.GetService<PseudonymContext>()!;

        var existingNamespace = new Data.Models.Namespace
        {
            Name = "existingNamespace",
            Description = "existing namespace description",
            PseudonymLength = 32,
            CreatedAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow,
            PseudonymGenerationMethod = PseudonymGenerationMethod.Unspecified,
            PseudonymPrefix = "",
            PseudonymSuffix = "",
        };

        db.Namespaces.AddRange(existingNamespace);
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var client = factory.CreateClient();

        var fhirUri = new Uri(client.BaseAddress!, "/v1/fhir");
        var fhirClient = new FhirClient(fhirUri, client, new() { PreferredFormat = ResourceFormat.Json });

        var p = new Parameters
        {
            Parameter = new List<Parameters.ParameterComponent>
            {
                new Parameters.ParameterComponent
                {
                    Name = "namespace",
                    Value = new FhirString("existingNamespace"),
                },
                new Parameters.ParameterComponent
                {
                    Name = "originalValue",
                    Value = new FhirString("test"),
                },
            }
        };

        var response = await fhirClient.WholeSystemOperationAsync("create-pseudonym", p);

        var parameterResponse = response.Should().BeOfType<Parameters>().Which;

        var pseudonymValue = parameterResponse.GetSingleValue<FhirString>("pseudonymValue").Value;

        pseudonymValue.Should().NotBeNull().And.NotBeEquivalentTo("test");
    }
}
