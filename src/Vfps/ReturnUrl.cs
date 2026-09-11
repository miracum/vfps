namespace Vfps;

/// <summary>
/// Resolves the "send the browser here afterwards" query parameter that the login and culture
/// endpoints accept (see Program.cs) to a URL that is always on this site.
///
/// The login endpoint is what makes this matter. It is anonymous, and its value ends up in
/// AuthenticationProperties.RedirectUri - which ASP.NET Core's remote-authentication handler
/// redirects to verbatim once sign-in completes, performing no local-URL check of its own. Passed
/// through unvalidated, a link to .../authentication/login?returnUrl=https://evil.example hands
/// the user to someone else's page the moment they finish a genuine login on this one, with this
/// site's own domain as the referrer.
/// </summary>
internal static class ReturnUrl
{
    /// <summary>Where a missing, empty or non-local value sends the browser instead.</summary>
    internal const string Fallback = "/ui";

    public static string Resolve(string? returnUrl)
    {
        // Every caller builds an absolute path on this site - NavigationManagerExtensions
        // .BuildLoginUrl and MainLayout's BuildCultureUrl both anchor theirs under "/ui", and the
        // cookie handler's own challenge emits OriginalPathBase + OriginalPath. Anything else is
        // a stale link or someone trying it on, and neither is worth honouring.
        if (string.IsNullOrEmpty(returnUrl) || returnUrl[0] != '/')
        {
            return Fallback;
        }

        // A browser strips tabs and newlines from a URL before resolving it, so "/\t/evil.example"
        // is fetched as "//evil.example". The check below therefore has to see the same string the
        // browser will, and refusing the characters it rewrites is the cheapest way to be sure of
        // that.
        foreach (var character in returnUrl)
        {
            if (char.IsControl(character))
            {
                return Fallback;
            }
        }

        // "//evil.example" is protocol-relative, and "/\evil.example" is the spelling of it every
        // browser also accepts: both are a different origin entirely despite that leading slash.
        // Same rule as ASP.NET Core's own IsLocalUrl, which is not public API to call directly.
        return returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\')
            ? returnUrl
            : Fallback;
    }
}
