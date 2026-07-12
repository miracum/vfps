using System.Security.Cryptography;

namespace Vfps.PseudonymGenerators;

public class FullRandomBase62EncodedGenerator : IPseudonymGenerator
{
    private const string Base62Alphabet =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public string GeneratePseudonym(string originalValue, uint pseudonymLength = 32)
    {
        return RandomNumberGenerator.GetString(Base62Alphabet, (int)pseudonymLength);
    }
}
