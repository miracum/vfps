using Vfps.Protos;

namespace Vfps.PseudonymGenerators;

public class PseudonymizationMethodsLookup
{
    // Typed as object rather than a shared marker interface: IPseudonymGenerator and
    // IDeterministicPseudonymGenerator represent genuinely different capabilities (does this
    // generator need the original value or not) and deliberately share no common base - see the
    // doc comments on each. Generate() below is the only place that needs to know which one it
    // got.
    private readonly IDictionary<PseudonymGenerationMethod, object> lookup;

    public PseudonymizationMethodsLookup()
    {
        lookup = new Dictionary<PseudonymGenerationMethod, object>()
        {
            { PseudonymGenerationMethod.Unspecified, new CryptoRandomBase64UrlEncodedGenerator() },
            {
                PseudonymGenerationMethod.SecureRandomBase64UrlEncoded,
                new CryptoRandomBase64UrlEncodedGenerator()
            },
            { PseudonymGenerationMethod.Sha256HexEncoded, new HexEncodedSha256HashGenerator() },
            { PseudonymGenerationMethod.Uuid4, new Uuid4Generator() },
            { PseudonymGenerationMethod.Uuid7, new Uuid7Generator() },
            { PseudonymGenerationMethod.FullRandomHexEncoded, new FullRandomHexEncodedGenerator() },
            {
                PseudonymGenerationMethod.FullRandomBase62Encoded,
                new FullRandomBase62EncodedGenerator()
            },
        };
    }

    public object this[PseudonymGenerationMethod method]
    {
        get { return lookup[method]; }
    }

    /// <summary>
    /// Generates a pseudonym for <paramref name="method"/>, passing <paramref name="originalValue"/>
    /// through only if the registered generator actually needs it. The single place that knows
    /// about the <see cref="IPseudonymGenerator"/>/<see cref="IDeterministicPseudonymGenerator"/>
    /// split, so callers (the gRPC/Blazor path and the FHIR facade) don't have to.
    /// </summary>
    public string Generate(
        PseudonymGenerationMethod method,
        string originalValue,
        uint pseudonymLength
    ) =>
        lookup[method] switch
        {
            IDeterministicPseudonymGenerator deterministic => deterministic.GeneratePseudonym(
                originalValue,
                pseudonymLength
            ),
            IPseudonymGenerator generator => generator.GeneratePseudonym(pseudonymLength),
            var other => throw new InvalidOperationException(
                $"Registered generator for '{method}' ({other.GetType()}) implements neither "
                    + $"{nameof(IPseudonymGenerator)} nor {nameof(IDeterministicPseudonymGenerator)}."
            ),
        };
}
