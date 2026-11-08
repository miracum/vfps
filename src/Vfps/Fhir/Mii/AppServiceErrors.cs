using System.Diagnostics.CodeAnalysis;
using Hl7.Fhir.Model;
using Vfps.AppServices;
using Vfps.Authorization;
using Vfps.PseudonymGenerators;

namespace Vfps.Fhir.Mii;

/// <summary>
/// The FHIR error each exception the app services throw is answered with. Shared by every version
/// of the MII API: a version changes how the operations' parameters are shaped, not what a
/// missing namespace or a refused permission means.
/// </summary>
internal static class AppServiceErrors
{
    /// <summary>
    /// Maps <paramref name="exception"/> to its FHIR error, if it is one the app services raise
    /// for a request they refuse. Anything else - a bug, an unreachable database - is left for the
    /// caller to propagate.
    /// </summary>
    public static bool TryMap(
        Exception exception,
        [NotNullWhen(true)] out FhirOperationResult? result
    )
    {
        result = exception switch
        {
            NamespaceNotFoundException ex => FhirOperationResult.Error(
                StatusCodes.Status404NotFound,
                OperationOutcome.IssueType.NotFound,
                $"The pseudonymization context '{ex.NamespaceName}' could not be found."
            ),
            ForbiddenException ex => FhirOperationResult.Error(
                StatusCodes.Status403Forbidden,
                OperationOutcome.IssueType.Forbidden,
                ex.Message
            ),
            // 422 for every rule a namespace enforces on a request that is itself well-formed -
            // the value fails its pattern, isn't a pseudonym in its parent, or the namespace's
            // settings don't allow what was asked of it.
            OriginalValueValidationException
            or ParentPseudonymNotFoundException
            or MultiplePseudonymsNotAllowedException
            or PseudonymGenerationMethodNotSupportedException => FhirOperationResult.Error(
                StatusCodes.Status422UnprocessableEntity,
                OperationOutcome.IssueType.BusinessRule,
                exception.Message
            ),
            PseudonymUpsertFailedException => FhirOperationResult.Error(
                StatusCodes.Status500InternalServerError,
                OperationOutcome.IssueType.Exception,
                "Failed to store the pseudonym after several retries."
            ),
            // Matched on the parameter, so an ArgumentException from a bug elsewhere still
            // surfaces as one. The operations reject these inputs before calling the app services,
            // which makes this a backstop rather than the check itself.
            ArgumentException { ParamName: "originalValue" or "count" } ex =>
                FhirOperationResult.Error(
                    StatusCodes.Status400BadRequest,
                    OperationOutcome.IssueType.Invalid,
                    ex.Message
                ),
            _ => null,
        };

        return result is not null;
    }
}
