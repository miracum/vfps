using System.Security.Claims;
using Vfps.Data.Models;

namespace Vfps.Authorization;

/// <summary>
/// The principal a vfps-issued access token authenticates as. One place for it because two
/// callers have to agree on it exactly: <see cref="AccessTokenAuthenticationHandler"/>, which
/// presents it on every request, and the Access tokens page, which resolves it against the grants
/// to show what a token can reach. Were the two built separately, the page could show access the
/// token doesn't actually have.
/// </summary>
public static class AccessTokenPrincipal
{
    public static ClaimsPrincipal Build(
        AccessToken token,
        string roleClaimType,
        string authenticationType
    )
    {
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
            authenticationType,
            nameType: "preferred_username",
            roleType: roleClaimType
        );

        return new ClaimsPrincipal(identity);
    }
}
