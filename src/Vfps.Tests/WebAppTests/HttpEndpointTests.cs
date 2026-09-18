using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Microsoft.Extensions.DependencyInjection;
using Task = System.Threading.Tasks.Task;

namespace Vfps.Tests.WebAppTests;

public class HttpEndpointTests(IntegrationTestFactory<Program, PseudonymContext> factory)
    : IClassFixture<IntegrationTestFactory<Program, PseudonymContext>>
{
    [Theory]
    [InlineData("/readyz")]
    [InlineData("/livez")]
    [InlineData("/healthz")]
    [InlineData("/")]
    public async Task HealthMetricsAndRootEndpoints_ShouldReturnSuccess(string endpoint)
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync(endpoint, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        response.Content.Should().NotBeNull();
    }

    /// <summary>
    /// The JSON-transcoded route for the Resolve RPC uses an AIP-136 custom-method suffix
    /// (<c>pseudonyms:resolve</c>), which is a shape nothing else in this proto uses - so the
    /// thing worth asserting is simply that ASP.NET routing serves it at all, rather than 404ing
    /// on the colon, and that it answers the two cases differently.
    /// </summary>
    [Fact]
    public async Task ResolvePseudonym_OverTheTranscodedRoute_ShouldAnswerFoundAndNotFound()
    {
        var db = factory.Services.GetService<PseudonymContext>()!;
        var namespaceName = $"resolve-route-{Guid.NewGuid():N}";
        db.Namespaces.Add(
            new Data.Models.Namespace
            {
                Name = namespaceName,
                Description = "resolve route test namespace",
                PseudonymLength = 32,
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
                PseudonymGenerationMethod = PseudonymGenerationMethod.Unspecified,
                PseudonymPrefix = "",
                PseudonymSuffix = "",
            }
        );
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(
            $"/v1/namespaces/{namespaceName}/pseudonyms",
            new { originalValue = "resolvable" },
            TestContext.Current.CancellationToken
        );
        created.EnsureSuccessStatusCode();
        var createdValue = JsonDocument
            .Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement.GetProperty("pseudonym")
            .GetProperty("pseudonymValue")
            .GetString();

        var resolved = await client.PostAsJsonAsync(
            $"/v1/namespaces/{namespaceName}/pseudonyms:resolve",
            new { originalValue = "resolvable" },
            TestContext.Current.CancellationToken
        );

        resolved.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument
            .Parse(await resolved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement.GetProperty("pseudonym")
            .GetProperty("pseudonymValue")
            .GetString()
            .Should()
            .Be(createdValue);

        var missing = await client.PostAsJsonAsync(
            $"/v1/namespaces/{namespaceName}/pseudonyms:resolve",
            new { originalValue = "never stored" },
            TestContext.Current.CancellationToken
        );

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
        var fhirClient = new FhirClient(
            fhirUri,
            client,
            new() { PreferredFormat = ResourceFormat.Json }
        );

        var p = new Parameters
        {
            Parameter = new List<Parameters.ParameterComponent>
            {
                new() { Name = "namespace", Value = new FhirString("existingNamespace") },
                new() { Name = "originalValue", Value = new FhirString("test") },
            },
        };

        var response = await fhirClient.WholeSystemOperationAsync("create-pseudonym", p);

        var parameterResponse = response.Should().BeOfType<Parameters>().Which;

        var pseudonymValue = parameterResponse.GetSingleValue<FhirString>("pseudonymValue")!.Value;

        pseudonymValue.Should().NotBeNull().And.NotBeEquivalentTo("test");
    }
}
