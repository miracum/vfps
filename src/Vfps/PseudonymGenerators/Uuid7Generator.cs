namespace Vfps.PseudonymGenerators;

public class Uuid7Generator : IPseudonymGenerator
{
    public string GeneratePseudonym(string originalValue, uint pseudonymLength = 36)
    {
        if (pseudonymLength != 36)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pseudonymLength),
                $"When using the {nameof(Uuid7Generator)}, the pseudonym length must be set to 36."
            );
        }

        return Guid.CreateVersion7().ToString();
    }
}
