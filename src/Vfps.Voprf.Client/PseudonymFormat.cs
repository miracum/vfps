namespace Vfps.Voprf.Client;

/// <summary>
/// How a pseudonym's bytes are rendered as text.
/// </summary>
public enum PseudonymFormat
{
    /// <summary>
    /// Unpadded base64url (RFC 4648 section 5). Safe in URLs, filenames, and CSV columns.
    /// Four characters per three bytes.
    /// </summary>
    Base64Url,

    /// <summary>Lowercase hexadecimal. Two characters per byte.</summary>
    Hex,
}
