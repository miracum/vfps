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
            { PseudonymGenerationMethod.Sha256HexEncoded, new HexEncodedSha256HashGenerator() },
            { PseudonymGenerationMethod.Uuid4, new Uuid4Generator() }
        };
    }

    public IPseudonymGenerator this[PseudonymGenerationMethod method]
    {
        get { return lookup[method]; }
    }
}
