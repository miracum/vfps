using Microsoft.Extensions.Options;
using Vfps.Voprf.Client;

namespace Vfps.PseudonymGenerators;

/// <summary>
/// Pseudonyms derived from the original value by a VOPRF server that never sees it.
/// </summary>
/// <remarks>
/// <para>
/// The value is blinded here, evaluated under a key vfps does not hold, and unblinded here again;
/// the key holder sees a uniformly random group element and cannot recover the value or tell that
/// two requests carried the same one. In exchange for that, vfps depends on a second service
/// being reachable to mint a pseudonym at all - which is why this generator exists only when one
/// is configured.
/// </para>
/// <para>
/// See <c>src/Vfps.Voprf.Client/README.md</c> for the client's own settings, and the server's
/// README for how a key rotation works. Rotating changes every pseudonym this generator produces,
/// so it is a data migration rather than a key swap.
/// </para>
/// </remarks>
public class VoprfPseudonymGenerator(
    IVoprfPseudonymizer pseudonymizer,
    IOptions<VoprfClientOptions> options
) : IValueDependentPseudonymGenerator, IHasFixedPseudonymLength
{
    private readonly VoprfClientOptions clientOptions = options.Value;

    /// <summary>
    /// The character length of every pseudonym this produces, fixed by the client's configured
    /// output length and encoding rather than chosen per namespace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike the random generators, the shape of a VOPRF pseudonym is a contract between every
    /// deployment sharing a key: the same value has to produce the same pseudonym everywhere, and
    /// truncating or re-encoding it differently in one namespace would break that silently.
    /// Exposing it through <see cref="IHasFixedPseudonymLength"/> means namespace creation
    /// rejects a mismatched length up front, using machinery that already exists for UUIDs.
    /// </para>
    /// <para>
    /// Change <c>Voprf:Length</c> or <c>Voprf:Format</c> and every pseudonym minted afterwards
    /// differs from the ones already stored - treat those two settings as being as fixed as the
    /// key itself. Unlike a key change, nothing in the stored value would reveal it.
    /// </para>
    /// <para>
    /// This counts the pseudonym only, not the <c>&lt;keyId&gt;.</c> that
    /// <c>Voprf:IncludeKeyIdInPseudonym</c> puts in front of it, so the stored string is longer
    /// than a namespace's declared <c>PseudonymLength</c>. That matches how the namespace's own
    /// prefix and suffix are treated, and it is what keeps a rotation from invalidating every
    /// namespace: a longer key id would otherwise change the required length of all of them.
    /// </para>
    /// </remarks>
    public uint FixedPseudonymLength =>
        clientOptions.Format switch
        {
            // Unpadded base64url: ceil(4n/3) characters for n bytes.
            PseudonymFormat.Base64Url => (uint)(((clientOptions.Length * 4) + 2) / 3),
            PseudonymFormat.Hex => (uint)clientOptions.Length * 2,
            _ => throw new InvalidOperationException(
                $"Unknown pseudonym format '{clientOptions.Format}'."
            ),
        };

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GeneratePseudonymsAsync(
        IReadOnlyList<string> originalValues,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(originalValues);

        if (originalValues.Count == 0)
        {
            return [];
        }

        // The client splits this across as many round trips as the server's batch cap requires.
        var pseudonyms = await pseudonymizer
            .PseudonymizeAsync(originalValues, cancellationToken)
            .ConfigureAwait(false);

        var values = new string[pseudonyms.Count];
        for (var i = 0; i < pseudonyms.Count; i++)
        {
            values[i] = pseudonyms[i].Value;
        }

        return values;
    }
}
