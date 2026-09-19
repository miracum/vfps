namespace Vfps.Authorization;

public static class AccessTokenDefaults
{
    /// <summary>
    /// The authentication scheme <see cref="AccessTokenAuthenticationHandler"/> is registered
    /// under. Registered alongside - not instead of - JWT bearer: an IdP-issued token and a
    /// vfps-issued one are both valid credentials for the API, and which handler sees a given
    /// request is decided by the token's own prefix.
    /// </summary>
    public const string AuthenticationScheme = "VfpsAccessToken";
}
