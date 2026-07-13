using System.Security.Cryptography;

namespace Vfps.PseudonymGenerators;

public class FullRandomHexEncodedGenerator : IPseudonymGenerator
{
    public string GeneratePseudonym(string originalValue, uint length = 32)
    {
        int byteCount = ((int)length + 1) / 2; // each byte yields 2 hex digits; round up for odd lengths

        string hex = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(byteCount));

        return hex[..(int)length]; // trim the extra digit when length is odd
    }
}
