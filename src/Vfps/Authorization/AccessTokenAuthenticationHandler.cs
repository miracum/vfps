using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.Data.Models;

namespace Vfps.Authorization;

/// <summary>
/// Authenticates the vfps-issued access tokens described in <see cref="AccessTokenSecret"/>.
///
/// The principal it produces is deliberately shaped like the one the JWT bearer handler produces,
/// because everything downstream - <see cref="NamespacePermissionChecker"/>, the audit subject in
/// <see cref="ClaimsPrincipalExtensions.GetSubject"/>, the app services' admin checks - reads
/// claims and knows nothing about how the request was authenticated. A personal access token
/// therefore replays its owner's identity claims and resolves to exactly the access they have;
/// a service-account token presents a principal that can only ever match a
/// <see cref="GranteeType.ServiceAccount"/> grant, since it carries no email and no roles at all.
/// </summary>
public class AccessTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AuthorizationConfig> authorizationConfig,
    IAccessTokenCache tokenCache,
    IAccessTokenUsageTracker usageTracker
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();

        if (!authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var presented = authorization[BearerPrefix.Length..].Trim();

        // NoResult, not Fail: a JWT reaching this handler (because a policy lists both schemes)
        // is somebody else's credential, not a bad one. Failing here would bury the JWT handler's
        // own, more useful result.
        if (!presented.StartsWith(AccessTokenSecret.Prefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        if (!AccessTokenSecret.TryParse(presented, out var tokenId, out var secret))
        {
            return AuthenticateResult.Fail("Malformed access token.");
        }

        var token = await tokenCache.FindAsync(tokenId, Context.RequestAborted);

        // Both misses are reported identically and without the secret: whether an id is unknown,
        // revoked, or simply paired with the wrong secret is not something an unauthenticated
        // caller gets to learn.
        if (token is null || !AccessTokenSecret.Verify(secret, token.TokenHash))
        {
            Logger.LogDebug("Access token {TokenId} was rejected.", tokenId);
            return AuthenticateResult.Fail("Unknown or revoked access token.");
        }

        var now = DateTimeOffset.UtcNow;
        if (!token.IsActiveAt(now))
        {
            Logger.LogDebug(
                "Access token {TokenId} expired at {ExpiresAt}.",
                tokenId,
                token.ExpiresAt
            );
            return AuthenticateResult.Fail("The access token has expired.");
        }

        usageTracker.Track(token.Id, now);

        var principal = BuildPrincipal(token);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    /// <summary>
    /// Answers with a bare 401 and a <c>WWW-Authenticate: Bearer</c> challenge, matching the JWT
    /// bearer handler - gRPC clients map that to <c>Unauthenticated</c>, and a browser redirect
    /// would be meaningless to every caller that presents a token.
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private ClaimsPrincipal BuildPrincipal(AccessToken token)
    {
        var roleClaimType = authorizationConfig.Value.RoleClaimType;
        List<Claim> claims = [new(VfpsClaimTypes.TokenId, token.TokenId)];

        if (token.TokenType == AccessTokenType.ServiceAccount)
        {
            // The "sub" is prefixed rather than being the bare account name so an audit log can
            // never confuse a service account with a user whose IdP subject happens to match.
            var accountName = token.ServiceAccountName ?? string.Empty;
            claims.Add(new Claim("sub", $"service-account:{accountName}"));
            claims.Add(new Claim("preferred_username", accountName));
            claims.Add(new Claim(VfpsClaimTypes.ServiceAccount, accountName));
        }
        else
        {
            claims.Add(new Claim("sub", token.Subject ?? string.Empty));

            if (!string.IsNullOrEmpty(token.Username))
            {
                claims.Add(new Claim("preferred_username", token.Username));
            }

            if (!string.IsNullOrEmpty(token.Email))
            {
                // Only a verified address is ever stored (see AccessTokenAppService), so replaying
                // the verification flag alongside it is what ClaimsPrincipalExtensions.GetEmail
                // needs to honour an email grant - and storing nothing when it was unverified is
                // what stops a token from matching one its owner's own session would not.
                claims.Add(new Claim("email", token.Email));
                claims.Add(new Claim("email_verified", "true"));
            }

            claims.AddRange(token.Roles.Select(role => new Claim(roleClaimType, role)));
        }

        var identity = new ClaimsIdentity(
            claims,
            Scheme.Name,
            nameType: "preferred_username",
            roleType: roleClaimType
        );

        return new ClaimsPrincipal(identity);
    }
}
