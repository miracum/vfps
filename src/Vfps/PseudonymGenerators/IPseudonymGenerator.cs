namespace Vfps.PseudonymGenerators;

/// <summary>
/// Generates a pseudonym independent of the original value - random/opaque generation, where
/// nothing about the input feeds into the output. Every registered generation method implements
/// this; there is deliberately no deterministic alternative (a former SHA-256-based one was
/// removed, since determinism is incompatible with a multi-psn namespace generating several
/// distinct pseudonyms for the same original value in one call - see
/// <see cref="Vfps.Data.Models.Namespace.AllowsMultiplePseudonyms"/>).
/// </summary>
public interface IPseudonymGenerator
{
    string GeneratePseudonym(uint pseudonymLength = 32);
}
