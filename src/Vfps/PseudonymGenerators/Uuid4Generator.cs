namespace Vfps.PseudonymGenerators;

public class Uuid4Generator : IPseudonymGenerator
{
    public string GeneratePseudonym(string originalValue, uint pseudonymLength = 36)
    {
        if (pseudonymLength != 36)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pseudonymLength),
                $"When using the {nameof(Uuid4Generator)}, the pseudonym length must be set to 36."
            );
        }

        return Guid.NewGuid().ToString();
    }
}
