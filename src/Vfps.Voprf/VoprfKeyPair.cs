using System.Security.Cryptography;
using Vfps.Voprf.Internal;

namespace Vfps.Voprf;

/// <summary>
/// The server's key pair: a private scalar and the public element derived from it.
/// </summary>
/// <remarks>
/// <para>
/// The private key is the whole secret. Anyone holding it can compute every output, and so
/// can re-identify any subject whose original value they can guess. Store it the way you
/// would store a signing key, and keep it away from the values it pseudonymises.
/// </para>
/// <para>
/// The public key is meant to be published, and clients must obtain it through a channel the
/// server cannot control per-client - pinned in configuration, or fetched once and compared
/// across clients. A client that accepts whatever public key the server offers it has
/// verified nothing: the server can simply hand it a public key matching whichever private
/// key it chose to use.
/// </para>
/// <para>
/// Rotating the key changes every output it has ever produced. A store of outputs cannot be
/// re-keyed without the original values, so version keys if you intend to rotate.
/// </para>
/// </remarks>
public sealed class VoprfKeyPair : IDisposable
{
    /// <summary>Length of the private key in bytes.</summary>
    public const int PrivateKeyLength = VoprfSuite.ScalarLength;

    /// <summary>Length of the public key in bytes.</summary>
    public const int PublicKeyLength = VoprfSuite.ElementLength;

    /// <summary>
    /// Shortest seed <see cref="Derive"/> accepts, RFC 9497's <c>Ns</c>.
    /// </summary>
    public const int MinimumSeedLength = 32;

    private readonly byte[] privateKey;
    private readonly byte[] publicKey;
    private bool disposed;

    private VoprfKeyPair(byte[] privateKey, byte[] publicKey)
    {
        this.privateKey = privateKey;
        this.publicKey = publicKey;
    }

    /// <summary>
    /// The public key, for publishing to clients. Not a secret.
    /// </summary>
    public ReadOnlySpan<byte> PublicKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return publicKey;
        }
    }

    internal ReadOnlySpan<byte> PrivateKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return privateKey;
        }
    }

    /// <summary>
    /// Generates a key pair from the system CSPRNG. RFC 9497 <c>GenerateKeyPair</c>.
    /// </summary>
    public static VoprfKeyPair Generate()
    {
        var scalar = Ristretto.RandomScalar();
        return new VoprfKeyPair(scalar, Ristretto.ScalarMultiplyGenerator(scalar));
    }

    /// <summary>
    /// Derives a key pair deterministically from a seed. RFC 9497 <c>DeriveKeyPair</c>.
    /// </summary>
    /// <param name="seed">The secret seed. Treat it exactly as you would the key itself.</param>
    /// <param name="keyInfo">
    /// A public label separating this key from others derived from the same seed - a
    /// tenant, a column, a key generation.
    /// </param>
    /// <remarks>
    /// Use this when the key must be reproducible from material you already hold, such as a
    /// secret store's seed plus a rotation label. The derivation is domain-separated from
    /// every other hash in the protocol.
    /// </remarks>
    public static VoprfKeyPair Derive(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> keyInfo)
    {
        // RFC 9497 specifies a seed of Ns bytes. Nothing in the derivation would fail on a
        // shorter one - it would simply produce a key with as little entropy as the seed had,
        // and a four-byte seed yields a key anyone can find by trying every four-byte seed.
        if (seed.Length < MinimumSeedLength)
        {
            throw new ArgumentException(
                $"A seed must be at least {MinimumSeedLength} bytes; got {seed.Length}. The "
                    + "derived key is only as unguessable as the seed behind it.",
                nameof(seed)
            );
        }

        if (keyInfo.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(keyInfo),
                keyInfo.Length,
                "RFC 9497 frames the key info length as uint16."
            );
        }

        // deriveInput = seed || I2OSP(len(info), 2) || info
        var deriveInput = new byte[seed.Length + 2 + keyInfo.Length + 1];
        seed.CopyTo(deriveInput);
        deriveInput[seed.Length] = (byte)(keyInfo.Length >> 8);
        deriveInput[seed.Length + 1] = (byte)keyInfo.Length;
        keyInfo.CopyTo(deriveInput.AsSpan(seed.Length + 2));

        try
        {
            // The counter occupies the final byte, rewritten each attempt. A zero scalar is
            // unusable and astronomically unlikely; the loop is the specification's.
            for (var counter = 0; counter <= byte.MaxValue; counter++)
            {
                deriveInput[^1] = (byte)counter;

                // Pinned from the moment it exists. Deriving into an ordinary array and copying
                // into a pinned one afterwards would leave the key wherever the GC had since
                // moved the original, which is exactly what pinning is meant to prevent.
                var scalar = VoprfSuite.HashToScalarForKeyDerivation(deriveInput);
                if (scalar.ContainsAnyExcept((byte)0))
                {
                    return new VoprfKeyPair(scalar, Ristretto.ScalarMultiplyGenerator(scalar));
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(deriveInput);
        }

        throw new VoprfException("DeriveKeyPair exhausted 256 counters without a usable scalar.");
    }

    /// <summary>
    /// Adopts an existing private key, for instance one read from a secret store, and
    /// recomputes the public key from it.
    /// </summary>
    /// <param name="privateKey">
    /// Exactly <see cref="PrivateKeyLength"/> bytes: a canonically reduced, non-zero scalar.
    /// </param>
    /// <exception cref="ArgumentException">The value is not a usable scalar.</exception>
    public static VoprfKeyPair Import(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != PrivateKeyLength)
        {
            throw new ArgumentException(
                $"A private key is {PrivateKeyLength} bytes, got {privateKey.Length}.",
                nameof(privateKey)
            );
        }

        if (!privateKey.ContainsAnyExcept((byte)0))
        {
            throw new ArgumentException("A private key must not be zero.", nameof(privateKey));
        }

        // Scalars at or above the group order are reduced inconsistently across
        // implementations, so the same key would not mean the same function everywhere.
        if (!IsLessThanGroupOrder(privateKey))
        {
            throw new ArgumentException(
                "A private key must be a scalar reduced modulo the ristretto255 group order.",
                nameof(privateKey)
            );
        }

        var copy = GC.AllocateArray<byte>(PrivateKeyLength, pinned: true);
        privateKey.CopyTo(copy);

        return new VoprfKeyPair(copy, Ristretto.ScalarMultiplyGenerator(copy));
    }

    /// <summary>Copies the private key out, for writing to a secret store.</summary>
    /// <param name="destination">Exactly <see cref="PrivateKeyLength"/> bytes.</param>
    public void ExportPrivateKey(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (destination.Length != PrivateKeyLength)
        {
            throw new ArgumentException(
                $"A private key is {PrivateKeyLength} bytes, got {destination.Length}.",
                nameof(destination)
            );
        }

        privateKey.CopyTo(destination);
    }

    /// <summary>
    /// Clears the private key. This shortens the window in which it sits in memory; it
    /// cannot undo copies the runtime may already have made.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(privateKey);
        disposed = true;
    }

    /// <summary>
    /// The ristretto255 group order L = 2^252 + 27742317777372353535851937790883648493,
    /// little-endian, as serialised scalars are.
    /// </summary>
    private static ReadOnlySpan<byte> GroupOrder =>
        [
            0xed,
            0xd3,
            0xf5,
            0x5c,
            0x1a,
            0x63,
            0x12,
            0x58,
            0xd6,
            0x9c,
            0xf7,
            0xa2,
            0xde,
            0xf9,
            0xde,
            0x14,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x10,
        ];

    private static bool IsLessThanGroupOrder(ReadOnlySpan<byte> scalar)
    {
        for (var i = PrivateKeyLength - 1; i >= 0; i--)
        {
            if (scalar[i] != GroupOrder[i])
            {
                return scalar[i] < GroupOrder[i];
            }
        }

        return false;
    }
}
