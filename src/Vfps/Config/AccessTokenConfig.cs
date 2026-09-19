namespace Vfps.Config;

/// <summary>
/// Long-lived, vfps-issued bearer credentials for callers that can't use the identity provider:
/// personal access tokens, which act as the user who created them, and service-account tokens,
/// which act as a named principal an admin granted access to.
///
/// Off by default, like every other optional subsystem in this codebase. A static credential is
/// strictly more exposed than a short-lived IdP-issued one - it is replayable for as long as it
/// lives - so a deployment that requires every caller to come through the IdP leaves this unset
/// and the whole feature, including its UI, stays invisible. Nested under
/// <see cref="AuthorizationConfig"/> because it is meaningless without it: with
/// <see cref="AuthorizationConfig.IsEnabled"/> false there is nothing to authenticate against.
/// </summary>
public class AccessTokenConfig
{
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Pre-filled lifetime for a newly created token. A token always expires - see
    /// <see cref="MaximumLifetime"/> for why there is no "never" option.
    /// </summary>
    public TimeSpan DefaultLifetime { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// The longest lifetime a token may be created with. Expiry is the one bound that holds
    /// without anybody noticing anything is wrong: a token whose owner has left, whose role was
    /// withdrawn at the IdP, or that was pasted into a CI log stops working on its own. Set to
    /// zero to lift the cap - tokens still expire, the creator just picks any date.
    /// </summary>
    public TimeSpan MaximumLifetime { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// How often each replica writes back the "last used" timestamps it has accumulated. The
    /// authentication path itself only records them in memory (see
    /// <see cref="Authorization.IAccessTokenUsageTracker"/>), so this decides how stale the
    /// column can be, not how much work a request does. Purely informational - nothing about
    /// authentication or authorization reads it.
    /// </summary>
    public TimeSpan UsageFlushInterval { get; set; } = TimeSpan.FromMinutes(1);
}
