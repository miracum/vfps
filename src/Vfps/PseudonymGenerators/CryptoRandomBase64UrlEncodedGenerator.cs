using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Vfps.PseudonymGenerators;

public class CryptoRandomBase64UrlEncodedGenerator : IPseudonymGenerator
{
    private static readonly byte[] hashedMachineNameBytes = SHA256.HashData(
        Encoding.UTF8.GetBytes(Environment.MachineName)
    );

    public CryptoRandomBase64UrlEncodedGenerator() { }

    public string GeneratePseudonym(string originalValue, uint pseudonymLength = 32)
    {
        var hashedMachineName = hashedMachineNameBytes.AsSpan(0, 4);

        var ticks = BitConverter.GetBytes(Environment.TickCount).AsSpan();

        var rand = RandomNumberGenerator.GetBytes((int)pseudonymLength).AsSpan();

        var buffer = new byte[hashedMachineName.Length + ticks.Length + rand.Length];

        hashedMachineName.CopyTo(buffer);
        ticks.CopyTo(buffer.AsSpan(hashedMachineName.Length));
        rand.CopyTo(buffer.AsSpan(hashedMachineName.Length + ticks.Length));

        return WebEncoders.Base64UrlEncode(buffer);
    }
}
