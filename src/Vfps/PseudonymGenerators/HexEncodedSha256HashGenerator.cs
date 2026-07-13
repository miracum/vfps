using System.Security.Cryptography;
using System.Text;

namespace Vfps.PseudonymGenerators;

public class HexEncodedSha256HashGenerator
    : IDeterministicPseudonymGenerator,
        IHasFixedPseudonymLength
{
    public uint FixedPseudonymLength => 64;

    public string GeneratePseudonym(string originalValue, uint pseudonymLength = 32)
    {
        if (pseudonymLength != FixedPseudonymLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pseudonymLength),
                $"When using the {nameof(HexEncodedSha256HashGenerator)}, the pseudonym length must be set to {FixedPseudonymLength}."
            );
        }

        var inputAsBytes = Encoding.UTF8.GetBytes(originalValue);
        var sha256Bytes = SHA256.HashData(inputAsBytes);

        return Convert.ToHexString(sha256Bytes);
    }
}
