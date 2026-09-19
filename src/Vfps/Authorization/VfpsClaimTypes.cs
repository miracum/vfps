namespace Vfps.Authorization;

/// <summary>
/// Claims vfps mints itself, on the principal an <see cref="Data.Models.AccessToken"/>
/// authenticates. Namespaced with a <c>vfps:</c> prefix so they can never collide with a claim an
/// identity provider issues - which matters because a personal access token replays its owner's
/// IdP claims verbatim alongside these.
/// </summary>
public static class VfpsClaimTypes
{
    /// <summary>
    /// The <see cref="Data.Models.ServiceAccount.Name"/> this principal is. Present only on a
    /// service-account token's principal, and the thing a
    /// <see cref="Data.Models.GranteeType.ServiceAccount"/> grant matches against.
    /// </summary>
    public const string ServiceAccount = "vfps:service_account";

    /// <summary>
    /// The <see cref="Data.Models.AccessToken.TokenId"/> the request was authenticated with.
    /// Present on both kinds of token principal, absent on a browser session or an IdP-issued
    /// JWT - which is what makes it the test for "this request came in on a static credential".
    /// </summary>
    public const string TokenId = "vfps:token_id";
}
