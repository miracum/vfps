using System.Net;
using System.Text;
using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Task = System.Threading.Tasks.Task;

namespace Vfps.Tests.WebAppTests;

/// <summary>
/// The MII pseudonymization API over HTTP, the way a FHIR client sees it: served at its base URL,
/// with batch Bundles posted to the base itself, and every error an OperationOutcome.
/// </summary>
public class MiiFhirApiTests(IntegrationTestFactory<Program, PseudonymContext> factory)
    : IClassFixture<IntegrationTestFactory<Program, PseudonymContext>>
{
    private const string BasePath = "/fhir/mii/2026";

    private static readonly JsonSerializerOptions FhirJsonOptions =
        new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);

    private string CreateNamespace(bool allowsMultiplePseudonyms = false)
    {
        var db = factory.Services.GetService<PseudonymContext>()!;
        var name = $"mii-{Guid.NewGuid():N}";
        db.Namespaces.Add(
            new Data.Models.Namespace
            {
                Name = name,
                Description = "MII FHIR API test namespace",
                PseudonymLength = 16,
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
                PseudonymGenerationMethod = PseudonymGenerationMethod.FullRandomHexEncoded,
                PseudonymPrefix = "",
                PseudonymSuffix = "",
                AllowsMultiplePseudonyms = allowsMultiplePseudonyms,
            }
        );
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return name;
    }

    private FhirClient CreateFhirClient()
    {
        var client = factory.CreateClient();
        return new FhirClient(
            new Uri(client.BaseAddress!, BasePath),
            client,
            new() { PreferredFormat = ResourceFormat.Json }
        );
    }

    private static Parameters ContextAndOriginal(string namespaceName, string originalValue) =>
        new Parameters()
            .Add("context", new Identifier("https://example.org/psn-contexts", namespaceName))
            .Add("original", new Identifier("https://example.org/patient-ids", originalValue));

    private static StringContent FhirJson(Resource resource) =>
        new(
            JsonSerializer.Serialize(resource, FhirJsonOptions),
            Encoding.UTF8,
            "application/fhir+json"
        );

    private static async Task<T> ReadResourceAsync<T>(HttpResponseMessage response)
        where T : Resource
    {
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonSerializer
            .Deserialize<Resource>(json, FhirJsonOptions)
            .Should()
            .BeOfType<T>()
            .Which;
    }

    [Fact]
    public async Task PseudonymizeThenGetPseudonym_WithAFhirClient_ShouldReturnTheSamePseudonym()
    {
        var namespaceName = CreateNamespace();
        var fhirClient = CreateFhirClient();

        var created = await fhirClient.WholeSystemOperationAsync(
            "pseudonymize",
            ContextAndOriginal(namespaceName, "a value")
        );
        var resolved = await fhirClient.WholeSystemOperationAsync(
            "get-pseudonym",
            ContextAndOriginal(namespaceName, "a value")
        );

        var pseudonym = created
            .Should()
            .BeOfType<Parameters>()
            .Which.GetSingleValue<Identifier>("pseudonym")!;
        pseudonym.System.Should().Be("https://example.org/psn-contexts");
        pseudonym.Value.Should().NotBeNullOrEmpty().And.NotBe("a value");
        resolved
            .Should()
            .BeOfType<Parameters>()
            .Which.GetSingleValue<Identifier>("pseudonym")!
            .Value.Should()
            .Be(pseudonym.Value);
    }

    [Fact]
    public async Task GetPseudonym_ForAnUnknownValue_ShouldAnswerNotFoundWithAnOperationOutcome()
    {
        var namespaceName = CreateNamespace();
        var fhirClient = CreateFhirClient();

        var act = () =>
            fhirClient.WholeSystemOperationAsync(
                "get-pseudonym",
                ContextAndOriginal(namespaceName, "never stored")
            );

        var thrown = (await act.Should().ThrowAsync<FhirOperationException>()).Which;
        thrown.Status.Should().Be(HttpStatusCode.NotFound);
        thrown
            .Outcome!.Issue.Should()
            .ContainSingle()
            .Which.Code.Should()
            .Be(OperationOutcome.IssueType.NotFound);
    }

    [Fact]
    public async Task PseudonymizeMultipleThenDePseudonymize_ShouldResolveEveryNewPseudonym()
    {
        var namespaceName = CreateNamespace(allowsMultiplePseudonyms: true);
        var fhirClient = CreateFhirClient();

        var created = await fhirClient.WholeSystemOperationAsync(
            "pseudonymize-multiple",
            ContextAndOriginal(namespaceName, "a value").Add("count", new Integer(2))
        );

        var pseudonyms = created.Should().BeOfType<Parameters>().Which.Get("pseudonym").ToList();
        pseudonyms.Should().HaveCount(2);
        foreach (var pseudonym in pseudonyms)
        {
            var value = pseudonym
                .Part.Single(p => p.Name == "value")
                .Value.Should()
                .BeOfType<Identifier>()
                .Which;

            var original = await fhirClient.WholeSystemOperationAsync(
                "de-pseudonymize",
                new Parameters()
                    .Add("context", new Identifier(null, namespaceName))
                    .Add("pseudonym", value)
            );

            original
                .Should()
                .BeOfType<Parameters>()
                .Which.GetSingle("original")!
                .Part.Single(p => p.Name == "value")
                .Value.Should()
                .BeOfType<Identifier>()
                .Which.Value.Should()
                .Be("a value");
        }
    }

    [Fact]
    public async Task BatchBundle_PostedToTheBase_ShouldBeAnsweredWithABatchResponse()
    {
        var namespaceName = CreateNamespace();
        var fhirClient = CreateFhirClient();
        var batch = new Bundle
        {
            Type = Bundle.BundleType.Batch,
            Entry =
            [
                new()
                {
                    FullUrl = $"urn:uuid:{Guid.NewGuid()}",
                    Resource = ContextAndOriginal(namespaceName, "first"),
                    Request = new() { Method = Bundle.HTTPVerb.POST, Url = "$pseudonymize" },
                },
                // An operation that doesn't exist, answered without touching the database:
                // entries run concurrently, and this host's in-memory database sits behind a
                // single connection that isn't safe to share between threads. The controller
                // tests, which run on a connection per context, cover concurrent entries.
                new()
                {
                    FullUrl = $"urn:uuid:{Guid.NewGuid()}",
                    Resource = ContextAndOriginal(namespaceName, "second"),
                    Request = new() { Method = Bundle.HTTPVerb.POST, Url = "$no-such-operation" },
                },
            ],
        };

        var response = await fhirClient.TransactionAsync(batch);

        response!.Type.Should().Be(Bundle.BundleType.BatchResponse);
        response.Entry.Select(e => e.Response!.Status).Should().Equal("200 OK", "404 Not Found");
        response.Entry[0].Resource.Should().BeOfType<Parameters>();
        response.Entry[1].Resource.Should().BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task Metadata_ShouldNameTheImplementationGuideRelease()
    {
        var capabilities = await CreateFhirClient().CapabilityStatementAsync();

        capabilities!
            .ImplementationGuide.Should()
            .Equal(
                "https://www.medizininformatik-initiative.de/fhir/ext/mii-interface-module-pseudonymization|2026.1.0"
            );
    }

    public static TheoryData<string, string> MalformedBodies =>
        new()
        {
            {
                "a Bundle where Parameters are expected",
                """{"resourceType": "Bundle", "type": "batch"}"""
            },
            { "JSON that is not a FHIR resource", """{"context": "a namespace"}""" },
            { "no JSON at all", "not json" },
        };

    /// <summary>
    /// A body the input formatter can't hand to the action as Parameters is still answered with
    /// an OperationOutcome - not with a ProblemDetails, nor, for a resource of another type, with
    /// the 500 a failed cast would cause.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task Operation_WithAMalformedBody_ShouldAnswerBadRequestWithAnOperationOutcome(
        string because,
        string body
    )
    {
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"{BasePath}/$pseudonymize",
            new StringContent(body, Encoding.UTF8, "application/fhir+json"),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
        (await ReadResourceAsync<OperationOutcome>(response)).Issue.Should().ContainSingle();
    }

    [Theory]
    [InlineData("$delete-pseudonym")]
    [InlineData("$anonymize-original")]
    public async Task NotYetImplementedOperation_ShouldAnswerNotImplemented(string operation)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"{BasePath}/{operation}",
            FhirJson(ContextAndOriginal("any", "value")),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        (await ReadResourceAsync<OperationOutcome>(response))
            .Issue.Should()
            .ContainSingle()
            .Which.Code.Should()
            .Be(OperationOutcome.IssueType.NotSupported);
    }

    [Fact]
    public async Task SwaggerDocument_ShouldDescribeTheOperations()
    {
        var client = factory.CreateClient();

        var document = await client.GetStringAsync(
            "/swagger/v1/swagger.json",
            TestContext.Current.CancellationToken
        );

        using var parsed = JsonDocument.Parse(document);
        var paths = parsed.RootElement.GetProperty("paths");
        paths.TryGetProperty($"{BasePath}/$pseudonymize", out _).Should().BeTrue();
        paths.TryGetProperty(BasePath, out _).Should().BeTrue();
    }
}
