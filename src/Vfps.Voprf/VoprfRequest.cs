using System.Security.Cryptography;

namespace Vfps.Voprf;

/// <summary>
/// One blinded input, waiting for the server's answer.
/// </summary>
/// <remarks>
/// Holds the blinding scalar and a copy of the input, neither of which leaves the client.
/// Keeping the input here rather than asking for it again at finalisation removes the chance
/// of finalising against a different one, which would silently produce an output belonging
/// to nothing.
/// </remarks>
public sealed class VoprfRequest : IDisposable
{
    private readonly byte[] blind;
    private readonly byte[] input;
    private bool disposed;

    internal VoprfRequest(byte[] blind, byte[] input, byte[] blindedElement)
    {
        this.blind = blind;
        this.input = input;
        BlindedElement = blindedElement;
    }

    /// <summary>
    /// The element to send to the server.
    /// </summary>
    /// <remarks>
    /// It is a uniformly random group element, independent of the input: the server can
    /// neither recover the input from it nor tell that two requests carried the same one.
    /// </remarks>
    public byte[] BlindedElement { get; }

    internal ReadOnlySpan<byte> Blind
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return blind;
        }
    }

    internal ReadOnlySpan<byte> Input
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return input;
        }
    }

    /// <summary>Clears the blinding scalar and the retained copy of the input.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(blind);
        CryptographicOperations.ZeroMemory(input);
        disposed = true;
    }
}
