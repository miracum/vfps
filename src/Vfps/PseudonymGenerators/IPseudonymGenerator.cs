namespace Vfps.PseudonymGenerators;

public interface IPseudonymGenerator
{
    string GeneratePseudonym(string originalValue, uint pseudonymLength = 32);
}
