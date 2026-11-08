using FhirParametersGenerator;
using Hl7.Fhir.Model;

// The operations' in and out parameters, as the IG's OperationDefinitions declare them.
// FhirParametersGenerator generates the mapping between each class and a Parameters resource -
// FromFhirParameters and ToFhirParameters - with every property standing for the parameter of the
// same, camel-cased name. The classes have to be public: the generated ToFhirParameters extension
// methods are.
//
// The in parameters are all nullable, whatever their cardinality in the IG: the generated parser
// leaves a parameter that is missing - or of another type than declared - null, and the
// controller answers that with an OperationOutcome naming it.

namespace Vfps.Fhir.Mii.V2026;

/// <summary>The in parameters of <c>$pseudonymize</c> and <c>$get-pseudonym</c>, which take the same ones.</summary>
[GenerateFhirParameters]
public partial class SinglePseudonymRequest
{
    /// <summary>The pseudonymization context - its value is the namespace name.</summary>
    public Identifier? Context { get; init; }

    /// <summary>The original value.</summary>
    public Identifier? Original { get; init; }
}

/// <summary>The out parameters of <c>$pseudonymize</c> and <c>$get-pseudonym</c>.</summary>
[GenerateFhirParameters]
public partial class SinglePseudonymResponse
{
    /// <summary>The context, exactly as received.</summary>
    public required Identifier Context { get; init; }

    /// <summary>The original value, exactly as received.</summary>
    public required Identifier Original { get; init; }

    /// <summary>The pseudonym.</summary>
    public required Identifier Pseudonym { get; init; }
}

/// <summary>The in parameters of <c>$pseudonymize-multiple</c>.</summary>
[GenerateFhirParameters]
public partial class PseudonymizeMultipleRequest
{
    /// <summary>The pseudonymization context - its value is the namespace name.</summary>
    public Identifier? Context { get; init; }

    /// <summary>The original value.</summary>
    public Identifier? Original { get; init; }

    /// <summary>How many pseudonyms to add, or 0 to return the ones already stored.</summary>
    public Integer? Count { get; init; }
}

/// <summary>The out parameters of <c>$pseudonymize-multiple</c>.</summary>
[GenerateFhirParameters]
public partial class PseudonymizeMultipleResponse
{
    /// <summary>
    /// One <c>pseudonym</c> parameter per pseudonym. Singular, because it names the repeated
    /// parameter rather than the list.
    /// </summary>
    public required List<PseudonymizeMultipleResponsePseudonym> Pseudonym { get; init; }
}

/// <summary>The parts of one <c>pseudonym</c> parameter of <see cref="PseudonymizeMultipleResponse"/>.</summary>
public class PseudonymizeMultipleResponsePseudonym
{
    /// <summary>The context, exactly as received.</summary>
    public required Identifier Context { get; init; }

    /// <summary>The original value, exactly as received.</summary>
    public required Identifier Original { get; init; }

    /// <summary>The pseudonym.</summary>
    public required Identifier Value { get; init; }
}

/// <summary>The in parameters of <c>$de-pseudonymize</c>.</summary>
[GenerateFhirParameters]
public partial class DePseudonymizeRequest
{
    /// <summary>The pseudonymization context - its value is the namespace name.</summary>
    public Identifier? Context { get; init; }

    /// <summary>The pseudonym to look up the original value of.</summary>
    public Identifier? Pseudonym { get; init; }
}

/// <summary>
/// The out parameters of <c>$de-pseudonymize</c>. The IG allows several <c>original</c>
/// parameters; vfps never stores more than one original value per pseudonym.
/// </summary>
[GenerateFhirParameters]
public partial class DePseudonymizeResponse
{
    /// <summary>The original value of the pseudonym.</summary>
    public required DePseudonymizeResponseOriginal Original { get; init; }
}

/// <summary>The parts of the <c>original</c> parameter of <see cref="DePseudonymizeResponse"/>.</summary>
public class DePseudonymizeResponseOriginal
{
    /// <summary>The context, exactly as received.</summary>
    public required Identifier Context { get; init; }

    /// <summary>The original value.</summary>
    public required Identifier Value { get; init; }

    /// <summary>The pseudonym, exactly as received.</summary>
    public required Identifier Pseudonym { get; init; }
}
