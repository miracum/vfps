using Vfps.Protos;

namespace Vfps.PseudonymGenerators;

public class PseudonymizationMethodsLookup
{
    private readonly IDictionary<PseudonymGenerationMethod, IPseudonymGenerator> lookup;
    private readonly IValueDependentPseudonymGenerator? valueDependentGenerator;

    /// <summary>
    /// Methods whose pseudonym is derived from the original value rather than generated
    /// independently of it - see <see cref="IValueDependentPseudonymGenerator"/>.
    /// </summary>
    private static readonly HashSet<PseudonymGenerationMethod> ValueDependentMethods =
    [
        PseudonymGenerationMethod.Voprf,
    ];

    /// <param name="valueDependentGenerator">
    /// The VOPRF generator, or null when no VOPRF server is configured - in which case
    /// <see cref="PseudonymGenerationMethod.Voprf"/> is simply not an available method, the same
    /// way a removed method isn't.
    /// </param>
    public PseudonymizationMethodsLookup(
        IValueDependentPseudonymGenerator? valueDependentGenerator = null
    )
    {
        this.valueDependentGenerator = valueDependentGenerator;

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
    /// Whether <paramref name="method"/> derives its pseudonym from the original value, and so
    /// must go through <see cref="GetValueDependentGenerator"/> rather than
    /// <see cref="Generate"/>.
    /// </summary>
    public static bool IsValueDependent(PseudonymGenerationMethod method) =>
        ValueDependentMethods.Contains(method);

    /// <summary>
    /// Whether <paramref name="method"/> can be used to create new pseudonyms right now. False
    /// for a method that has been removed, and for VOPRF when no server is configured.
    /// </summary>
    public bool IsSupported(PseudonymGenerationMethod method) =>
        IsValueDependent(method) ? valueDependentGenerator is not null : lookup.ContainsKey(method);

    /// <summary>
    /// The generator for a value-dependent <paramref name="method"/>.
    /// </summary>
    /// <exception cref="PseudonymGenerationMethodNotSupportedException">
    /// No VOPRF server is configured, so nothing can evaluate the method.
    /// </exception>
    public IValueDependentPseudonymGenerator GetValueDependentGenerator(
        PseudonymGenerationMethod method
    )
    {
        if (!IsValueDependent(method) || valueDependentGenerator is null)
        {
            throw new PseudonymGenerationMethodNotSupportedException(method);
        }

        return valueDependentGenerator;
    }

    /// <summary>
    /// The required pseudonym length for <paramref name="method"/>, or null if it's freely
    /// configurable. Backed by <see cref="IHasFixedPseudonymLength"/> on the registered generator
    /// itself, so this can never drift out of sync with what the generator actually enforces.
    /// Used to validate a namespace's PseudonymLength upfront at namespace-creation time (see
    /// NamespaceAppService.CreateAsync) and to drive the admin UI's namespace-creation form.
    /// </summary>
    public uint? GetFixedPseudonymLength(PseudonymGenerationMethod method) =>
        IsValueDependent(method)
            ? (GetValueDependentGenerator(method) as IHasFixedPseudonymLength)?.FixedPseudonymLength
            : (this[method] as IHasFixedPseudonymLength)?.FixedPseudonymLength;

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
/// <see cref="PseudonymGenerationMethod"/> that has no registered generator - an existing
/// namespace created before a generation method was removed (e.g. the former SHA-256 method), or
/// a VOPRF namespace on a deployment with no VOPRF server configured. Reading pseudonyms already
/// stored under such a namespace is unaffected; only generating a *new* one fails.
/// </summary>
public class PseudonymGenerationMethodNotSupportedException(PseudonymGenerationMethod method)
    : Exception(
        $"The pseudonym generation method '{method}' is no longer supported for creating new pseudonyms."
    )
{
    public PseudonymGenerationMethod Method { get; } = method;
}
