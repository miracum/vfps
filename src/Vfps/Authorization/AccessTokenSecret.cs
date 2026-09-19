using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Vfps.Data.Models;

namespace Vfps.Authorization;

/// <summary>
/// The token string itself: how one is minted, recognised and verified.
///
/// Shape is <c>vfps_pat_&lt;id&gt;.&lt;secret&gt;</c> (or <c>vfps_sat_</c> for a service-account
/// token) - a fixed, greppable prefix so a leaked token is findable by a secret scanner and
/// obvious in a log, a public id the server looks the row up by, and 256 bits of randomness that
/// only ever exists in plaintext once.
///
/// The id and the secret are separated by a dot rather than another underscore because both are
/// base64url, whose alphabet includes <c>_</c> - splitting on an underscore would cut a token
/// whose id happens to contain one in the wrong place. A dot cannot occur in either half.
/// </summary>
public static class AccessTokenSecret
{
    /// <summary>What every vfps-issued token starts with, whatever its type.</summary>
    public const string Prefix = "vfps_";

    private const string PersonalPrefix = "vfps_pat_";
    private const string ServiceAccountPrefix = "vfps_sat_";

    private const char SecretSeparator = '.';

    private const int TokenIdBytes = 16;
    private const int SecretBytes = 32;

    /// <summary>
    /// A newly minted token. <paramref name="Presented"/> is the whole string the caller puts in
    /// an Authorization header - the only time it exists, since only <paramref name="Hash"/> is
    /// ever stored.
    /// </summary>
    public readonly record struct GeneratedToken(string TokenId, byte[] Hash, string Presented);

    public static GeneratedToken Generate(AccessTokenType tokenType)
    {
        var tokenId = RandomBase64Url(TokenIdBytes);
        var secret = RandomBase64Url(SecretBytes);
        var prefix = tokenType == AccessTokenType.Personal ? PersonalPrefix : ServiceAccountPrefix;

        return new GeneratedToken(
            tokenId,
            Hash(secret),
            $"{prefix}{tokenId}{SecretSeparator}{secret}"
        );
    }

    /// <summary>
    /// Splits a presented token into its public id and its secret. False for anything that isn't
    /// shaped like a vfps token at all - which is not an authentication failure worth reporting,
    /// just a credential meant for a different handler.
    /// </summary>
    public static bool TryParse(string? presented, out string tokenId, out string secret)
    {
        tokenId = string.Empty;
        secret = string.Empty;

        if (presented is null)
        {
            return false;
        }

        // The type prefix isn't checked against the row it resolves to: the id is what identifies
        // the token, and a mismatched prefix can only ever fail the hash comparison anyway.
        var body =
            presented.StartsWith(PersonalPrefix, StringComparison.Ordinal)
                ? presented[PersonalPrefix.Length..]
            : presented.StartsWith(ServiceAccountPrefix, StringComparison.Ordinal)
                ? presented[ServiceAccountPrefix.Length..]
            : null;

        if (body is null)
        {
            return false;
        }

        var separator = body.IndexOf(SecretSeparator);
        if (separator <= 0 || separator == body.Length - 1)
        {
            return false;
        }

        var candidateSecret = body[(separator + 1)..];

        // Exactly one separator: a second one means the string was mangled somewhere between
        // being issued and being presented, not that part of it is a secret containing a dot.
        // Nothing is assigned until the whole shape checks out, so a caller that ignores the
        // return value can't read a half-parsed id out of the out parameters.
        if (candidateSecret.Contains(SecretSeparator))
        {
            return false;
        }

        tokenId = body[..separator];
        secret = candidateSecret;
        return true;
    }

    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    /// <summary>
    /// Whether <paramref name="secret"/> is the one <paramref name="expectedHash"/> was taken
    /// from, compared in constant time so the comparison itself reveals nothing about how much of
    /// a guess was right.
    /// </summary>
    public static bool Verify(string secret, byte[] expectedHash) =>
        CryptographicOperations.FixedTimeEquals(Hash(secret), expectedHash);

    private static string RandomBase64Url(int byteCount)
    {
        Span<byte> bytes = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }
}
