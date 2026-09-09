namespace Vfps.Components;

/// <summary>
/// Human-readable names for the pseudonym generation methods. Shared by the namespace create
/// form's picker and the namespace details view so the two can't drift apart.
/// </summary>
public static class PseudonymGenerationMethodDisplay
{
    /// <summary>
    /// Derived from the enum itself rather than a hand-maintained list, so a newly-added
    /// generation method can't be forgotten here the way UUIDv7 previously was.
    /// </summary>
    public static IReadOnlyList<Protos.PseudonymGenerationMethod> Selectable { get; } =
    [
        .. Enum.GetValues<Protos.PseudonymGenerationMethod>()
            .Where(method => method != Protos.PseudonymGenerationMethod.Unspecified),
    ];

    public static string FriendlyName(Protos.PseudonymGenerationMethod method) =>
        method switch
        {
            Protos.PseudonymGenerationMethod.SecureRandomBase64UrlEncoded =>
                "Secure random (Base64 URL-safe)",
            Protos.PseudonymGenerationMethod.Uuid4 => "UUIDv4",
            Protos.PseudonymGenerationMethod.Uuid7 => "UUIDv7",
            Protos.PseudonymGenerationMethod.FullRandomHexEncoded => "Full random (hex)",
            Protos.PseudonymGenerationMethod.FullRandomBase62Encoded => "Full random (Base62)",
            Protos.PseudonymGenerationMethod.FullRandomBase32Encoded => "Full random (Base32)",
            _ => method.ToString(),
        };
}
