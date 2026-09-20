namespace Vfps.PseudonymGenerators;

/// <summary>
/// Generates a pseudonym <em>from</em> the original value, rather than independently of it.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="IPseudonymGenerator"/>, and deliberately a separate interface
/// rather than an extra parameter on that one. The two differ in more than their arguments: this
/// kind is deterministic, needs the value it is pseudonymizing, and - because the work happens in
/// another service - is asynchronous and can fail for reasons that have nothing to do with the
/// value. Widening <see cref="IPseudonymGenerator"/> to fit would have made six purely local,
/// synchronous generators pretend to be none of those things.
/// </para>
/// <para>
/// Determinism is why a namespace using one of these cannot allow multiple pseudonyms per
/// original value: there is exactly one pseudonym for a value, so a request for a second distinct
/// one has no answer. <see cref="AppServices.NamespaceAppService"/> refuses that combination at
/// namespace-creation time.
/// </para>
/// </remarks>
public interface IValueDependentPseudonymGenerator
{
    /// <summary>
    /// Generates pseudonyms for several values at once, in the order given.
    /// </summary>
    /// <remarks>
    /// Batched rather than one-at-a-time because the implementation talks to another service over
    /// the network: a CSV chunk is a thousand values by default, and a round trip each would make
    /// this method the whole cost of a job.
    /// </remarks>
    Task<IReadOnlyList<string>> GeneratePseudonymsAsync(
        IReadOnlyList<string> originalValues,
        CancellationToken cancellationToken
    );
}
