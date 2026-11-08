using Hl7.Fhir.Model;

namespace Vfps.Fhir.Mii;

/// <summary>
/// What a FHIR operation answers with: an HTTP status and a resource - the operation's output on
/// success, an OperationOutcome otherwise. Kept apart from an MVC action result so that one
/// implementation of an operation answers both a direct request and an entry of a batch Bundle
/// (see <see cref="FhirBatch"/>), which carries the same two things per entry.
/// </summary>
internal sealed record FhirOperationResult(int StatusCode, Resource Resource)
{
    public static FhirOperationResult Ok(Resource resource) =>
        new(StatusCodes.Status200OK, resource);

    public static FhirOperationResult Error(
        int statusCode,
        OperationOutcome.IssueType code,
        string diagnostics
    ) =>
        new(
            statusCode,
            new OperationOutcome
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
            }
        );
}
