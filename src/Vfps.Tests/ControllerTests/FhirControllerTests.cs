using FakeItEasy;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Vfps.Data;
using Vfps.Fhir;
using Vfps.Tests.ServiceTests;
using Task = System.Threading.Tasks.Task;

namespace Vfps.Tests.ControllerTests;

public class FhirControllerTests : ServiceTestBase
{
    private readonly FhirController sut;

    public FhirControllerTests()
    {
        sut = new FhirController(A.Fake<ILogger<FhirController>>(),
            new NamespaceRepository(InMemoryPseudonymContext),
            new PseudonymRepository(InMemoryPseudonymContext));
    }

    [Fact]
    public async Task CreatePseudonym_WithEmptyBody_ShouldReturnErrorOutcome()
    {
        var p = new Parameters();

        var response = await sut.CreatePseudonym(p);

        response.Should().BeOfType<BadRequestObjectResult>()
            .Which
            .Value
            .Should().BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task CreatePseudonym_WithNull_ShouldReturnErrorOutcome()
    {
        var response = await sut.CreatePseudonym(null);

        response.Should().BeOfType<BadRequestObjectResult>()
            .Which
            .Value
            .Should().BeOfType<OperationOutcome>();
    }

    [Fact]
    public async Task CreatePseudonym_WithExistingNamespaceRequested_ShouldSucceed()
    {
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

        var response = await sut.CreatePseudonym(p);

        var parameterResponse = response.Should().BeOfType<OkObjectResult>()
            .Which
            .Value
            .Should().BeOfType<Parameters>().Which;

        var pseudonymValue = parameterResponse.GetSingleValue<FhirString>("pseudonymValue").Value;

        pseudonymValue.Should().NotBeNull().And.NotBeEquivalentTo("test");
    }

    [Fact]
    public void GetMetadata_ShouldReturnCapabilityStatement()
    {
        var response = sut.GetMetadata();

        response.Should().BeOfType<CapabilityStatement>();
    }
}
