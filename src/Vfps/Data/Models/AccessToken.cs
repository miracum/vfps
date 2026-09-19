namespace Vfps.Data.Models;

/// <summary>
/// What an <see cref="AccessToken"/> authenticates as.
/// </summary>
public enum AccessTokenType
{
    /// <summary>
    /// A personal access token. Authenticates as the user who created it, replaying the identity
    /// they held at that moment (<see cref="AccessToken.Subject"/>, <see cref="AccessToken.Email"/>,
    /// <see cref="AccessToken.Roles"/>), so it resolves to exactly the same namespace access they
    /// have - no more, and no less, since the grants themselves are still read live.
    /// </summary>
    Personal,

    /// <summary>
    /// A service-account token. Authenticates as <see cref="AccessToken.ServiceAccountName"/>,
    /// whose access an admin granted explicitly.
    /// </summary>
    ServiceAccount,
}

/// <summary>
/// One issued credential. The secret itself is never stored - only
/// <see cref="TokenHash"/>, the SHA-256 of the random half of the token string - so a database
/// dump yields nothing usable, and a token that has been lost can only be replaced, never
/// recovered.
///
/// Both token types share this table because everything about the credential itself is shared:
/// the format, the hashing, expiry, revocation, and the single authentication handler that
/// verifies them. What differs is only which principal the token stands for, which is what
/// <see cref="TokenType"/> selects between - the identity-snapshot columns are set for a
/// <see cref="AccessTokenType.Personal"/> token and <see cref="ServiceAccountName"/> for a
/// <see cref="AccessTokenType.ServiceAccount"/> one.
/// </summary>
public class AccessToken : TracksCreationAndUpdates
{
    public Guid Id { get; set; }

    public AccessTokenType TokenType { get; set; }

    /// <summary>
    /// The public half of the token string, used to look the row up. Distinct from
    /// <see cref="Id"/> so the value that travels in an Authorization header, appears in logs and
    /// is shown in the UI is never the database key, and so a failed authentication can be
    /// attributed to a token without anyone having to hold the secret to do it.
    /// </summary>
    public required string TokenId { get; set; }

    /// <summary>
    /// SHA-256 of the token's secret half. A plain hash rather than a password KDF on purpose:
    /// the secret is 256 bits of <see cref="System.Security.Cryptography.RandomNumberGenerator"/>
    /// output, so there is no dictionary to grind and nothing for a salt to defend against, while
    /// a deliberately slow KDF would put its cost on every single authenticated pseudonym Create.
    /// </summary>
    public required byte[] TokenHash { get; set; }

    /// <summary>A human-chosen label, e.g. "nightly ETL run". Not unique.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// When this token stops authenticating. Always set: see
    /// <see cref="Config.AccessTokenConfig.MaximumLifetime"/>.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// When the token was revoked, or null while it is live. Revoked rows are kept rather than
    /// deleted so the audit trail survives the credential.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Roughly when the token was last presented - written back periodically by
    /// <see cref="Authorization.IAccessTokenUsageTracker"/>, never on the request path, and lagging
    /// by up to <see cref="Config.AccessTokenConfig.UsageFlushInterval"/>. Informational only: it
    /// exists so an admin can tell a token still in use from one nobody has touched in months.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>The <c>sub</c> of whoever created this token.</summary>
    public required string CreatedBy { get; set; }

    /// <summary>
    /// The owning user's <c>sub</c>, for a <see cref="AccessTokenType.Personal"/> token; null for
    /// a service-account one. This is the identity the token authenticates as, and what "my
    /// tokens" is listed by.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>The owning user's <c>preferred_username</c>, for display.</summary>
    public string? Username { get; set; }

    /// <summary>
    /// The owning user's verified email address at the time of creation, or null if the IdP
    /// hadn't verified one - matching <see cref="Authorization.ClaimsPrincipalExtensions.GetEmail"/>,
    /// so a token can never match an email grant its owner's own session wouldn't.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// The owning user's role claims at the time of creation. Replayed on every request made with
    /// this token, which is what lets role grants resolve without an IdP round trip - and is also
    /// the reason tokens expire: a role withdrawn at the IdP is not reflected here, so the token
    /// has to be revoked (or run out) for the change to reach it.
    /// </summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>
    /// The service account this token authenticates as, for a
    /// <see cref="AccessTokenType.ServiceAccount"/> token; null for a personal one.
    /// </summary>
    public string? ServiceAccountName { get; set; }

    public ServiceAccount? ServiceAccount { get; set; }

    /// <summary>Whether the token is neither revoked nor expired at <paramref name="asOf"/>.</summary>
    public bool IsActiveAt(DateTimeOffset asOf) => RevokedAt is null && ExpiresAt > asOf;
}
