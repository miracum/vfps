namespace Vfps.PseudonymGenerators;

public class Uuid4Generator : IPseudonymGenerator, IHasFixedPseudonymLength
{
    public uint FixedPseudonymLength => 36;

    public string GeneratePseudonym(uint pseudonymLength = 36)
    {
        if (pseudonymLength != FixedPseudonymLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pseudonymLength),
                $"When using the {nameof(Uuid4Generator)}, the pseudonym length must be set to {FixedPseudonymLength}."
            );
        }

        return Guid.NewGuid().ToString();
    }
}
