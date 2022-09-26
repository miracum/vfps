using Microsoft.AspNetCore.WebUtilities;
using System.Security.Cryptography;
using System.Text;

namespace Vfps.PseudonymGenerators;

public class CryptoRandomBase64UrlEncodedGenerator : IPseudonymGenerator
{
    private readonly byte[] hashedMachineNameBytes;

    public CryptoRandomBase64UrlEncodedGenerator()
    {
        hashedMachineNameBytes = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName));
    }

    public string GeneratePseudonym(string originalValue, uint pseudonymLength)
    {
        var ticks = BitConverter.GetBytes(Environment.TickCount64).AsSpan(3, 4);

        var hashedMachineNameSpan = hashedMachineNameBytes.AsSpan(0, 4);

        var rand = RandomNumberGenerator.GetBytes((int)pseudonymLength).AsSpan();

        byte[] buffer = new byte[hashedMachineNameSpan.Length + ticks.Length + rand.Length];

        var span = new Span<byte>(buffer, 0, buffer.Length);

        int index = 0;
        ticks.CopyTo(span.Slice(index, ticks.Length));
        index += ticks.Length;

        hashedMachineNameSpan.CopyTo(span.Slice(index, hashedMachineNameSpan.Length));
        index += hashedMachineNameSpan.Length;

        rand.CopyTo(span.Slice(index, rand.Length));

        return WebEncoders.Base64UrlEncode(span);
    }
}
