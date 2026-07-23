using System.Globalization;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using Vfps.AppServices;
using Vfps.Authorization;

namespace Vfps.Fhir;

/// <summary>
/// Endpoint for the FHIR-based API.
/// </summary>
[ApiController]
[Route("v1/fhir")]
[Produces("application/fhir+json")]
[Consumes("application/fhir+json", "application/json")]
public class FhirController(
    ILogger<FhirController> logger,
    IPseudonymAppService pseudonymAppService
) : ControllerBase
{
    /// <summary>
    /// Create a pseudonym for an original value in the given namespace.
    /// </summary>
    /// <param name="parametersResource">A FHIR Parameters resource</param>
    /// <param name="cancellationToken">A cancellation token to abort the request</param>
    /// <returns>Either a FHIR Parameters resource containing the created pseudonym or a FHIR OperationOutcome in case of errors.</returns>
    [HttpPost("$create-pseudonym")]
    [ProducesResponseType(typeof(Parameters), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    [ProducesResponseType(typeof(OperationOutcome), 403)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    [ProducesResponseType(typeof(OperationOutcome), 500)]
    public async Task<ObjectResult> CreatePseudonym(
        [FromBody] Parameters? parametersResource,
        CancellationToken cancellationToken = default
    )
    {
        if (parametersResource is null)
        {
            var outcome = new OperationOutcome();
            outcome.Issue.Add(
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = OperationOutcome.IssueType.Processing,
                    Diagnostics = "Received malformed or missing resource",
                }
            );
            logger.LogError("Bad Request: received request body is empty.");
            return BadRequest(outcome);
        }

        var namespaceName = parametersResource.GetSingleValue<FhirString>("namespace")?.Value;
        var originalValue = parametersResource.GetSingleValue<FhirString>("originalValue")?.Value;

        if (
            namespaceName is null
            || originalValue is null
            || string.IsNullOrWhiteSpace(originalValue)
        )
        {
            var outcome = new OperationOutcome();
            outcome.Issue.Add(
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = OperationOutcome.IssueType.Processing,
                    Diagnostics =
                        "namespace and/or originalValue are missing or blank in the Parameters request object",
                }
            );
            return BadRequest(outcome);
        }

        // Goes through the same app service (and therefore the same namespace-scoped write-access
        // check and pseudonym generation logic) as the gRPC/Blazor path - this facade must not
        // have its own, weaker copy of either.
        Data.Models.Pseudonym upsertedPseudonym;
        try
        {
            upsertedPseudonym = await pseudonymAppService.CreateAsync(
                namespaceName,
                originalValue,
                User,
                cancellationToken
            );
        }
        catch (NamespaceNotFoundException)
        {
            var outcome = new OperationOutcome();
            outcome.Issue.Add(
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = OperationOutcome.IssueType.Processing,
                    Diagnostics = $"the namespace '{namespaceName}' could not be found.",
                }
            );
            return NotFound(outcome);
        }
        catch (ForbiddenException ex)
        {
            var outcome = new OperationOutcome();
            outcome.Issue.Add(
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = OperationOutcome.IssueType.Forbidden,
                    Diagnostics = ex.Message,
                }
            );
            return StatusCode(403, outcome);
        }
        catch (ArgumentException)
        {
            // Defense in depth - the blank-originalValue case above already returns BadRequest
            // before this point is ever reached in practice.
            var outcome = new OperationOutcome();
            outcome.Issue.Add(
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = OperationOutcome.IssueType.Processing,
                    Diagnostics = "originalValue must not be blank.",
                }
            );
            return BadRequest(outcome);
        }
        catch (PseudonymUpsertFailedException)
        {
            var outcome = new OperationOutcome();
            outcome.Issue.Add(
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = OperationOutcome.IssueType.Processing,
                    Diagnostics = "failed to store the pseudonym after several retries",
                }
            );
            return StatusCode(500, outcome);
        }

        return Ok(
            new Parameters
            {
                Parameter = new List<Parameters.ParameterComponent>
                {
                    new() { Name = "namespace", Value = new FhirString(namespaceName) },
                    new() { Name = "originalValue", Value = new FhirString(originalValue) },
                    new()
                    {
                        Name = "pseudonymValue",
                        Value = new FhirString(upsertedPseudonym.PseudonymValue),
                    },
                },
            }
        );
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
            Software = new CapabilityStatement.SoftwareComponent { Name = "VFPS FHIR API" },
            FhirVersion = FHIRVersion.N4_0_1,
            Format = new[] { "application/fhir+json" },
            Rest = new List<CapabilityStatement.RestComponent>
            {
                new() { Mode = CapabilityStatement.RestfulCapabilityMode.Server },
            },
        };
    }
}
