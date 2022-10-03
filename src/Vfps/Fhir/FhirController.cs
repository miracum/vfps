using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using Vfps.Data;
using Vfps.PseudonymGenerators;

namespace Vfps.Fhir;

/// <summary>
/// Endpoint for the FHIR-based API.
/// </summary>
[ApiController]
[Route("v1/fhir")]
[Produces("application/fhir+json")]
[Consumes("application/fhir+json", "application/json")]
public class FhirController : ControllerBase
{
    private readonly ILogger<FhirController> logger;

    public FhirController(ILogger<FhirController> logger, INamespaceRepository namespaceRepository, IPseudonymRepository pseudonymRepository)
    {
        this.logger = logger;
        NamespaceRepository = namespaceRepository;
        PseudonymRepository = pseudonymRepository;
    }

    private INamespaceRepository NamespaceRepository { get; }
    private IPseudonymRepository PseudonymRepository { get; }
    private PseudonymizationMethodsLookup Lookup { get; } = new PseudonymizationMethodsLookup();

    /// <summary>
    /// Create a pseudonym for an original value in the given namespace.
    /// </summary>
    /// <param name="parametersResource">A FHIR Parameters resource</param>
    /// <returns>Either a FHIR Parameters resource containing the created pseudonym or a FHIR OperationOutcome in case of errors.</returns>
    [HttpPost("$create-pseudonym")]
    [ProducesResponseType(typeof(Parameters), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    [ProducesResponseType(typeof(OperationOutcome), 500)]
    public async Task<ObjectResult> CreatePseudonym([FromBody] Parameters parametersResource)
    {
        if (parametersResource is null)
        {
            var outcome = new OperationOutcome();
            outcome.Issue.Add(new OperationOutcome.IssueComponent
            {
                Severity = OperationOutcome.IssueSeverity.Error,
                Code = OperationOutcome.IssueType.Processing,
                Diagnostics = "Received malformed or missing resource"
            });
            logger.LogError("Bad Request: received request body is empty.");
            return BadRequest(outcome);
        }

        var namespaceName = parametersResource.GetSingleValue<FhirString>("namespace")?.Value;
        var originalValue = parametersResource.GetSingleValue<FhirString>("originalValue")?.Value;

        if (namespaceName is null || originalValue is null)
        {
            var outcome = new OperationOutcome();
            outcome.Issue.Add(new OperationOutcome.IssueComponent
            {
                Severity = OperationOutcome.IssueSeverity.Error,
                Code = OperationOutcome.IssueType.Processing,
                Diagnostics = "namespace and/or originalValue are missing in the Parameters request object"
            });
            return BadRequest(outcome);
        }

        var @namespace = await NamespaceRepository.FindAsync(namespaceName);
        if (@namespace is null)
        {
            var outcome = new OperationOutcome();
            outcome.Issue.Add(new OperationOutcome.IssueComponent
            {
                Severity = OperationOutcome.IssueSeverity.Error,
                Code = OperationOutcome.IssueType.Processing,
                Diagnostics = $"the namespace '{namespaceName}' could not be found."
            });
            return NotFound(outcome);
        }

        var generator = Lookup[@namespace.PseudonymGenerationMethod];

        var pseudonymValue = string.Empty;

        using (var activity = Program.ActivitySource.StartActivity("GeneratePseudonym"))
        {
            activity?.SetTag("Method", generator.GetType().Name);

            pseudonymValue = generator.GeneratePseudonym(originalValue, @namespace.PseudonymLength);
            pseudonymValue = $"{@namespace.PseudonymPrefix}{pseudonymValue}{@namespace.PseudonymSuffix}";
        }

        var now = DateTimeOffset.UtcNow;
        var pseudonym = new Data.Models.Pseudonym()
        {
            CreatedAt = now,
            LastUpdatedAt = now,
            NamespaceName = @namespace.Name,
            OriginalValue = originalValue,
            PseudonymValue = pseudonymValue,
        };

        Data.Models.Pseudonym? upsertedPseudonym = await PseudonymRepository.CreateIfNotExist(pseudonym);

        if (upsertedPseudonym is null)
        {
            var outcome = new OperationOutcome();
            outcome.Issue.Add(new OperationOutcome.IssueComponent
            {
                Severity = OperationOutcome.IssueSeverity.Error,
                Code = OperationOutcome.IssueType.Processing,
                Diagnostics = $"failed to store the pseudonym after several retries"
            });
            return StatusCode(500, outcome);
        }

        return Ok(new Parameters
        {
            Parameter = new List<Parameters.ParameterComponent>
            {
                new Parameters.ParameterComponent
                {
                    Name = "namespace",
                    Value = new FhirString(namespaceName),
                },
                new Parameters.ParameterComponent
                {
                    Name = "originalValue",
                    Value = new FhirString(originalValue),
                },
                new Parameters.ParameterComponent
                {
                    Name = "pseudonymValue",
                    Value = new FhirString(upsertedPseudonym.PseudonymValue),
                },
            }
        });
    }

    /// <summary>
    ///     Returns the server's FHIR CapabilityStatement.
    ///     Note that this CapabilityStatement is not valid at this point as it does not include the custom operations.
    /// </summary>
    /// <returns>The server's FHIR CapabilityStatement.</returns>
    [HttpGet("metadata")]
    public CapabilityStatement GetMetadata()
    {
        return new()
        {
            Status = PublicationStatus.Active,
            Date = DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture),
            Kind = CapabilityStatementKind.Instance,
            Software = new CapabilityStatement.SoftwareComponent
            {
                Name = "VFPS FHIR API",
            },
            FhirVersion = FHIRVersion.N4_0_1,
            Format = new[] { "application/fhir+json" },
            Rest = new List<CapabilityStatement.RestComponent>
            {
                new ()
                {
                    Mode = CapabilityStatement.RestfulCapabilityMode.Server
                }
            },
        };
    }
}
