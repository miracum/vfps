using Vfps.Protos;

namespace Vfps.PseudonymGenerators;

public class PseudonymizationMethodsLookup
{
    private readonly IDictionary<PseudonymGenerationMethod, IPseudonymGenerator> lookup;

    public PseudonymizationMethodsLookup()
    {
        lookup = new Dictionary<PseudonymGenerationMethod, IPseudonymGenerator>()
        {
            { PseudonymGenerationMethod.Unspecified, new CryptoRandomBase64UrlEncodedGenerator() },
            {
                PseudonymGenerationMethod.SecureRandomBase64UrlEncoded,
                new CryptoRandomBase64UrlEncodedGenerator()
            },
            { PseudonymGenerationMethod.Uuid4, new Uuid4Generator() },
            { PseudonymGenerationMethod.Uuid7, new Uuid7Generator() },
            { PseudonymGenerationMethod.FullRandomHexEncoded, new FullRandomHexEncodedGenerator() },
            {
                PseudonymGenerationMethod.FullRandomBase62Encoded,
                new FullRandomBase62EncodedGenerator()
            },
            {
                PseudonymGenerationMethod.FullRandomBase32Encoded,
                new FullRandomBase32EncodedGenerator()
            },
        };
    }

    public IPseudonymGenerator this[PseudonymGenerationMethod method]
    {
        get
        {
            if (!lookup.TryGetValue(method, out var generator))
            {
                throw new PseudonymGenerationMethodNotSupportedException(method);
            }

            return generator;
        }
    }

    /// <summary>
    /// The required pseudonym length for <paramref name="method"/>, or null if it's freely
    /// configurable. Backed by <see cref="IHasFixedPseudonymLength"/> on the registered generator
    /// itself, so this can never drift out of sync with what the generator actually enforces.
    /// Used to validate a namespace's PseudonymLength upfront at namespace-creation time (see
    /// NamespaceAppService.CreateAsync) and to drive the admin UI's namespace-creation form.
    /// </summary>
    public uint? GetFixedPseudonymLength(PseudonymGenerationMethod method) =>
        (this[method] as IHasFixedPseudonymLength)?.FixedPseudonymLength;

    /// <summary>
    /// Generates a pseudonym for <paramref name="method"/>. Every registered generator is
    /// non-deterministic (ignores the original value entirely) - the last deterministic method,
    /// SHA-256, was removed because determinism is incompatible with a multi-psn namespace
    /// generating several *distinct* pseudonyms for the same original value in one call.
    /// </summary>
    public string Generate(PseudonymGenerationMethod method, uint pseudonymLength) =>
        this[method].GeneratePseudonym(pseudonymLength);
}

/// <summary>
/// Thrown by <see cref="PseudonymizationMethodsLookup"/> when asked to use a
/// <see cref="PseudonymGenerationMethod"/> that has no registered generator - notably, an
/// existing namespace created before a generation method was removed (e.g. the former SHA-256
/// method). Reading pseudonyms already stored under such a namespace is unaffected; only
/// generating a *new* one fails.
/// </summary>
public class PseudonymGenerationMethodNotSupportedException(PseudonymGenerationMethod method)
    : Exception(
        $"The pseudonym generation method '{method}' is no longer supported for creating new pseudonyms."
    )
{
    public PseudonymGenerationMethod Method { get; } = method;
}
