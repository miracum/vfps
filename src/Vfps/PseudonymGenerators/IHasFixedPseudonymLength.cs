namespace Vfps.PseudonymGenerators;

/// <summary>
/// Implemented by generators whose output length isn't actually configurable (a UUID is always
/// 36 characters, a SHA-256 hex digest is always 64) - <see cref="PseudonymizationMethodsLookup.GetFixedPseudonymLength"/>
/// uses this so callers (namespace creation, the admin UI) can know and enforce the required
/// length upfront, rather than only discovering a mismatch when a generator itself rejects it.
/// </summary>
public interface IHasFixedPseudonymLength
{
    uint FixedPseudonymLength { get; }
}
