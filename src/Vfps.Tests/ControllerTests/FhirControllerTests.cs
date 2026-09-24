using System.Security.Claims;
using FakeItEasy;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Vfps.Config;
using Vfps.Fhir;
using Vfps.Tests.ServiceTests;
using Task = System.Threading.Tasks.Task;

namespace Vfps.Tests.ControllerTests;

public class FhirControllerTests : ServiceTestBase
{
    private readonly FhirController sut;

    public FhirControllerTests()
    {
        sut = CreateSut();
    }

    private FhirController CreateSut(AuthorizationConfig? config = null)
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var controller = new FhirController(
            A.Fake<ILogger<FhirController>>(),
            CreatePseudonymAppService(namespaceRepository, pseudonymRepository, config)
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal() },
            },
        };

        return controller;
    }

    private static Parameters ResolveRequest(string namespaceName, string originalValue) =>
        new()
        {
            Parameter = new List<Parameters.ParameterComponent>
            {
                new() { Name = "namespace", Value = new FhirString(namespaceName) },
                new() { Name = "originalValue", Value = new FhirString(originalValue) },
            },
        };

    [Fact]
    public async Task ResolvePseudonym_WithAStoredOriginalValue_ShouldReturnItsExistingPseudonym()
    {
        var response = await sut.ResolvePseudonym(
            ResolveRequest("existingNamespace", "an original value"),
            TestContext.Current.CancellationToken
        );

        var parameters = response
            .Should()
            .BeOfType<OkObjectResult>()
            .Which.Value.Should()
            .BeOfType<Parameters>()
            .Which;
        parameters
            .GetSingleValue<FhirString>("pseudonymValue")!
            .Value.Should()
            .Be("existingPseudonym");
    }

    [Fact]
    public async Task ResolvePseudonym_WithAnUnknownOriginalValue_ShouldReturnNotFoundAndCreateNothing()
    {
        var countBefore = InMemoryPseudonymContext.Pseudonyms.Count();

        var response = await sut.ResolvePseudonym(
            ResolveRequest("existingNamespace", "never stored"),
            TestContext.Current.CancellationToken
        );

        var outcome = response
            .Should()
            .BeOfType<NotFoundObjectResult>()
            .Which.Value.Should()
            .BeOfType<OperationOutcome>()
            .Which;

        // The diagnostics reach the caller's own logs and error reporting, so they name the
        // namespace but never the value that was not found.
        outcome
            .Issue[0]
            .Diagnostics.Should()
            .NotContain("never stored")
            .And.Contain("existingNamespace");

        InMemoryPseudonymContext.Pseudonyms.Count().Should().Be(countBefore);
    }

    [Fact]
    public async Task ResolvePseudonym_WithUnknownNamespace_ShouldReturnNotFoundOutcome()
    {
        var response = await sut.ResolvePseudonym(
            ResolveRequest("noSuchNamespace", "an original value"),
            TestContext.Current.CancellationToken
        );

        response
            .Should()
            .BeOfType<NotFoundObjectResult>()
            .Which.Value.Should()
            .BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task ResolvePseudonym_WithBlankOriginalValue_ShouldReturnErrorOutcome()
    {
        var response = await sut.ResolvePseudonym(
            ResolveRequest("existingNamespace", "   "),
            TestContext.Current.CancellationToken
        );

        response
            .Should()
            .BeOfType<BadRequestObjectResult>()
            .Which.Value.Should()
            .BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task ResolvePseudonym_WithNull_ShouldReturnErrorOutcome()
    {
        var response = await sut.ResolvePseudonym(null, TestContext.Current.CancellationToken);

        response
            .Should()
            .BeOfType<BadRequestObjectResult>()
            .Which.Value.Should()
            .BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task CreatePseudonym_WithEmptyBody_ShouldReturnErrorOutcome()
    {
        var p = new Parameters();

        var response = await sut.CreatePseudonym(p, TestContext.Current.CancellationToken);

        response
            .Should()
            .BeOfType<BadRequestObjectResult>()
            .Which.Value.Should()
            .BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task CreatePseudonym_WithNull_ShouldReturnErrorOutcome()
    {
        var response = await sut.CreatePseudonym(null, TestContext.Current.CancellationToken);

        response
            .Should()
            .BeOfType<BadRequestObjectResult>()
            .Which.Value.Should()
            .BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task CreatePseudonym_WithBlankOriginalValue_ShouldReturnErrorOutcome()
    {
        var p = new Parameters
        {
            Parameter = new List<Parameters.ParameterComponent>
            {
                new() { Name = "namespace", Value = new FhirString("existingNamespace") },
                new() { Name = "originalValue", Value = new FhirString("   ") },
            },
        };

        var response = await sut.CreatePseudonym(p, TestContext.Current.CancellationToken);

        response
            .Should()
            .BeOfType<BadRequestObjectResult>()
            .Which.Value.Should()
            .BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task CreatePseudonym_WithExistingNamespaceRequested_ShouldSucceed()
    {
        var p = new Parameters
        {
            Parameter = new List<Parameters.ParameterComponent>
            {
                new() { Name = "namespace", Value = new FhirString("existingNamespace") },
                new() { Name = "originalValue", Value = new FhirString("test") },
            },
        };

        var response = await sut.CreatePseudonym(p, TestContext.Current.CancellationToken);

        var parameterResponse = response
            .Should()
            .BeOfType<OkObjectResult>()
            .Which.Value.Should()
            .BeOfType<Parameters>()
            .Which;

        var pseudonymValue = parameterResponse.GetSingleValue<FhirString>("pseudonymValue")!.Value;

        pseudonymValue.Should().NotBeNull().And.NotBeEquivalentTo("test");
    }

    [Fact]
    public async Task CreatePseudonym_WithNonMatchingValidationRegex_ShouldReturnUnprocessableEntityOutcome()
    {
        InMemoryPseudonymContext.Namespaces.Add(
            new Data.Models.Namespace
            {
                Name = "namespaceWithRegex",
                PseudonymLength = 16,
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
                OriginalValueValidationRegex = "^[0-9]+$",
            }
        );
        InMemoryPseudonymContext.SaveChanges();
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var p = new Parameters
        {
            Parameter = new List<Parameters.ParameterComponent>
            {
                new() { Name = "namespace", Value = new FhirString("namespaceWithRegex") },
                new() { Name = "originalValue", Value = new FhirString("not-a-number") },
            },
        };

        var response = await sut.CreatePseudonym(p, TestContext.Current.CancellationToken);

        response
            .Should()
            .BeOfType<UnprocessableEntityObjectResult>()
            .Which.Value.Should()
            .BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task CreatePseudonym_WithNonExistingNamespace_ShouldReturnNotFoundOutcome()
    {
        var p = new Parameters
        {
            Parameter = new List<Parameters.ParameterComponent>
            {
                new() { Name = "namespace", Value = new FhirString("notExisting") },
                new() { Name = "originalValue", Value = new FhirString("test") },
            },
        };

        var response = await sut.CreatePseudonym(p, TestContext.Current.CancellationToken);

        response
            .Should()
            .BeOfType<NotFoundObjectResult>()
            .Which.Value.Should()
            .BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task CreatePseudonym_WithAuthorizationEnabledAndNoWriteAccess_ShouldReturnForbiddenOutcome()
    {
        // Regression test: this endpoint used to call INamespaceRepository/IPseudonymRepository
        // directly, bypassing IPseudonymAppService (and therefore every write-access check)
        // entirely - any caller could create a pseudonym in any namespace regardless of grants.
        var restrictedSut = CreateSut(
            new AuthorizationConfig { IsEnabled = true, AdminRoles = ["admin"] }
        );
        var p = new Parameters
        {
            Parameter = new List<Parameters.ParameterComponent>
            {
                new() { Name = "namespace", Value = new FhirString("existingNamespace") },
                new() { Name = "originalValue", Value = new FhirString("test") },
            },
        };

        var response = await restrictedSut.CreatePseudonym(
            p,
            TestContext.Current.CancellationToken
        );

        response.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        response.Value.Should().BeOfType<OperationOutcome>();
    }

    [Fact]
    public void GetMetadata_ShouldReturnCapabilityStatement()
    {
        var response = sut.GetMetadata();

        response.Should().BeOfType<CapabilityStatement>();
    }
}
