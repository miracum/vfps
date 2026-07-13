namespace Vfps.PseudonymGenerators;

/// <summary>
/// Generates a pseudonym derived from the original value (e.g. a hash) - the same input always
/// produces the same pseudonym. Deliberately not related to <see cref="IPseudonymGenerator"/> by
/// inheritance: the two represent genuinely different capabilities, and a shared base would force
/// one side to implement a method it has no use for.
/// </summary>
public interface IDeterministicPseudonymGenerator
{
    string GeneratePseudonym(string originalValue, uint pseudonymLength = 32);
}
