using System.Security.Cryptography;

namespace Vfps.PseudonymGenerators;

public class FullRandomHexEncodedGenerator : IPseudonymGenerator
{
    public string GeneratePseudonym(string originalValue, uint pseudonymLength = 32)
    {
        var byteCount = (int)((pseudonymLength + 1) / 2);
        var randomBytes = RandomNumberGenerator.GetBytes(byteCount);

        return Convert.ToHexString(randomBytes)[..(int)pseudonymLength];
    }
}
