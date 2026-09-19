using System.Security.Claims;
using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <summary>
/// A newly issued token. <see cref="Secret"/> is the only time the credential exists in
/// plaintext - it is shown once and never recoverable, since only its hash is stored.
/// </summary>
public record IssuedAccessToken(AccessToken Token, string Secret);

/// <summary>
/// Issuing, listing and revoking the two kinds of vfps-issued credential.
///
/// The two differ in who may create one, and that difference is the whole design:
/// <list type="bullet">
/// <item>
/// A <see cref="AccessTokenType.Personal"/> token is self-service - any signed-in user may create
/// one - because it confers nothing they don't already have. It replays their identity, so it
/// resolves against the same grants their browser session does.
/// </item>
/// <item>
/// A <see cref="AccessTokenType.ServiceAccount"/> token is admin-only, because the principal it
/// authenticates as holds access granted to it explicitly, independent of any person.
/// </item>
/// </list>
///
/// No token may ever create another one, whatever its owner's permissions - see
/// <see cref="Authorization.ClaimsPrincipalExtensions.IsAccessTokenPrincipal"/>.
/// </summary>
public interface IAccessTokenAppService
{
    /// <summary>The caller's own personal access tokens, revoked and expired ones included.</summary>
    Task<IReadOnlyList<AccessToken>> GetMineAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>Every token in the deployment, of both kinds. Admin only.</summary>
    Task<IReadOnlyList<AccessToken>> GetAllAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>Every token issued for one service account. Admin only.</summary>
    Task<IReadOnlyList<AccessToken>> GetForServiceAccountAsync(
        string serviceAccountName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Issues a personal access token for the caller, capturing the identity they hold right now
    /// - subject, verified email and roles.
    /// </summary>
    /// <exception cref="ArgumentException">The label is blank, or the lifetime is out of range.</exception>
    /// <exception cref="Authorization.ForbiddenException">
    /// The caller is not a signed-in user: access tokens are off, authorization is off (so there
    /// is no identity to capture), the caller is a service account, or the request is itself
    /// authenticated with an access token.
    /// </exception>
    Task<IssuedAccessToken> CreatePersonalAsync(
        string name,
        TimeSpan lifetime,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>Issues a token that authenticates as a service account. Admin only.</summary>
    /// <exception cref="ServiceAccountNotFoundException">No such account.</exception>
    Task<IssuedAccessToken> CreateForServiceAccountAsync(
        string serviceAccountName,
        string name,
        TimeSpan lifetime,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Stops a token authenticating, keeping the row for audit. Permitted to the owner of a
    /// personal token and to any admin; a service-account token's is an admin-only operation.
    /// Revoking an already-revoked token is a no-op rather than an error.
    /// </summary>
    /// <exception cref="AccessTokenNotFoundException">No token with this id exists.</exception>
    Task RevokeAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a token's row outright, audit trail included. Same permissions as
    /// <see cref="RevokeAsync"/>; meant for tidying up long-revoked entries, not for turning a
    /// credential off - deleting a *live* token revokes it as a side effect, but says nothing
    /// about when or why.
    /// </summary>
    /// <exception cref="AccessTokenNotFoundException">No token with this id exists.</exception>
    Task DeleteAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken);
}

/// <summary>Thrown when addressing a token that doesn't exist.</summary>
public class AccessTokenNotFoundException(Guid id)
    : Exception($"No access token with the id '{id}' exists.")
{
    public Guid Id { get; } = id;
}
