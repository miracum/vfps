namespace Vfps.Voprf.Client;

/// <summary>
/// A pseudonym and the key generation that produced it.
/// </summary>
/// <param name="Value">The pseudonym, encoded per <see cref="VoprfClientOptions.Format"/>.</param>
/// <param name="KeyId">
/// The generation of the server's key. Store it alongside the pseudonym.
/// </param>
/// <remarks>
/// <see cref="KeyId"/> travels with the value because a pseudonym is meaningless without knowing
/// which key produced it. Rotating a key does not re-key a store - the old pseudonyms simply
/// belong to the old key - so a store that did not record which generation each row came from
/// cannot be migrated: there is no way to tell which rows are already done. It costs one column.
/// </remarks>
public readonly record struct VoprfPseudonym(string Value, string KeyId)
{
    /// <summary>The pseudonym itself, for callers that already know the generation.</summary>
    public override string ToString() => Value;
}
