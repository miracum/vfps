using System.Security.Cryptography;

namespace Vfps.PseudonymGenerators;

/// <summary>
/// Generates a pseudonym as a string of uniformly random characters drawn from the Base32
/// alphabet defined in RFC 4648, section 6
/// (https://datatracker.ietf.org/doc/html/rfc4648#section-6).
/// </summary>
public class FullRandomBase32EncodedGenerator : IPseudonymGenerator
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public string GeneratePseudonym(uint pseudonymLength = 32)
    {
        return RandomNumberGenerator.GetString(Base32Alphabet, (int)pseudonymLength);
    }
}
