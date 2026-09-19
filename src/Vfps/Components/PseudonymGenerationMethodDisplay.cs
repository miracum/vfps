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

    /// <summary>
    /// <see cref="Selectable"/> narrowed to what <paramref name="methodsLookup"/> can actually
    /// generate with. VOPRF is only available where a VOPRF server is configured, and offering it
    /// otherwise puts a choice in the form whose only outcome is an error on submit.
    /// </summary>
    public static IReadOnlyList<Protos.PseudonymGenerationMethod> SelectableFor(
        PseudonymGenerators.PseudonymizationMethodsLookup methodsLookup
    )
    {
        ArgumentNullException.ThrowIfNull(methodsLookup);
        return [.. Selectable.Where(methodsLookup.IsSupported)];
    }

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
            // Named for what makes it unlike every other entry: it is the only deterministic
            // one, so the same original value always yields the same pseudonym.
            Protos.PseudonymGenerationMethod.Voprf => "Deterministic (VOPRF server)",
            _ => method.ToString(),
        };
}
