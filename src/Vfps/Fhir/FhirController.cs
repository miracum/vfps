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
    [ProducesResponseType(typeof(OperationOutcome), 422)]
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
            // count: 1 - this facade always returns exactly one pseudonym value. For a multi-psn
            // namespace (Namespace.AllowsMultiplePseudonyms) that already has more than one
            // pseudonym stored for this original value, this is always the first one
            // (sequence number 0); it never creates additional ones.
            var upsertedPseudonyms = await pseudonymAppService.CreateAsync(
                namespaceName,
                originalValue,
                1,
                User,
                cancellationToken
            );
            upsertedPseudonym = upsertedPseudonyms[0];
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
        catch (OriginalValueValidationException ex)
        {
            // Unlike the malformed/missing-field cases above (400), the request itself is
            // well-formed - originalValue just fails a business rule the namespace enforces, so
            // 422 fits better than 400 here.
            var outcome = new OperationOutcome();
            outcome.Issue.Add(
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = OperationOutcome.IssueType.BusinessRule,
                    Diagnostics = ex.Message,
                }
            );
            return UnprocessableEntity(outcome);
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
    /// Resolve the pseudonym already stored for an original value, creating nothing.
    /// </summary>
    /// <param name="parametersResource">A FHIR Parameters resource</param>
    /// <param name="cancellationToken">A cancellation token to abort the request</param>
    /// <returns>
    /// Either a FHIR Parameters resource containing the existing pseudonym or a FHIR
    /// OperationOutcome - including a 404 when the namespace holds no pseudonym for the value,
    /// which is the difference from $create-pseudonym.
    /// </returns>
    [HttpPost("$resolve-pseudonym")]
    [ProducesResponseType(typeof(Parameters), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    [ProducesResponseType(typeof(OperationOutcome), 403)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    [ProducesResponseType(typeof(OperationOutcome), 422)]
    public async Task<ObjectResult> ResolvePseudonym(
        [FromBody] Parameters? parametersResource,
        CancellationToken cancellationToken = default
    )
    {
        if (parametersResource is null)
        {
            logger.LogError("Bad Request: received request body is empty.");
            return BadRequest(
                Outcome(
                    OperationOutcome.IssueType.Processing,
                    "Received malformed or missing resource"
                )
            );
        }

        var namespaceName = parametersResource.GetSingleValue<FhirString>("namespace")?.Value;
        var originalValue = parametersResource.GetSingleValue<FhirString>("originalValue")?.Value;

        if (
            namespaceName is null
            || originalValue is null
            || string.IsNullOrWhiteSpace(originalValue)
        )
        {
            return BadRequest(
                Outcome(
                    OperationOutcome.IssueType.Processing,
                    "namespace and/or originalValue are missing or blank in the Parameters request object"
                )
            );
        }

        // Same app service, and therefore the same write-access check, as $create-pseudonym and
        // the gRPC Resolve - this facade must not have its own, weaker copy of either.
        IReadOnlyList<Data.Models.Pseudonym> pseudonyms;
        try
        {
            pseudonyms = await pseudonymAppService.ResolveAsync(
                namespaceName,
                originalValue,
                User,
                cancellationToken
            );
        }
        catch (NamespaceNotFoundException)
        {
            return NotFound(
                Outcome(
                    OperationOutcome.IssueType.Processing,
                    $"the namespace '{namespaceName}' could not be found."
                )
            );
        }
        catch (ForbiddenException ex)
        {
            return StatusCode(403, Outcome(OperationOutcome.IssueType.Forbidden, ex.Message));
        }
        catch (ArgumentException)
        {
            return BadRequest(
                Outcome(OperationOutcome.IssueType.Processing, "originalValue must not be blank.")
            );
        }
        catch (OriginalValueValidationException ex)
        {
            // 422 rather than 400 for the same reason as $create-pseudonym: the request is
            // well-formed, the value just fails a rule the namespace enforces.
            return UnprocessableEntity(
                Outcome(OperationOutcome.IssueType.BusinessRule, ex.Message)
            );
        }

        if (pseudonyms.Count == 0)
        {
            // Deliberately without the original value in the diagnostics, unlike the namespace
            // name: an OperationOutcome travels into the caller's own logs and error reporting,
            // and the value is the thing this service exists to keep out of both.
            return NotFound(
                Outcome(
                    OperationOutcome.IssueType.NotFound,
                    $"the namespace '{namespaceName}' holds no pseudonym for the requested original value."
                )
            );
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
                        Value = new FhirString(pseudonyms[0].PseudonymValue),
                    },
                },
            }
        );
    }

    private static OperationOutcome Outcome(OperationOutcome.IssueType code, string diagnostics) =>
        new()
        {
            Issue =
            [
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = code,
                    Diagnostics = diagnostics,
                },
            ],
        };

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
