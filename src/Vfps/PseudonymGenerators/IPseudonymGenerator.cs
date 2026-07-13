namespace Vfps.PseudonymGenerators;

/// <summary>
/// Generates a pseudonym independent of the original value - random/opaque generation, where
/// nothing about the input feeds into the output. See <see cref="IDeterministicPseudonymGenerator"/>
/// for the alternative where the original value drives the generated pseudonym.
/// </summary>
public interface IPseudonymGenerator
{
    string GeneratePseudonym(uint pseudonymLength = 32);
}
