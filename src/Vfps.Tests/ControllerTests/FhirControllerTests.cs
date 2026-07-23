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
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var pseudonymRepository = new PseudonymRepository(InMemoryPseudonymContext);
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
