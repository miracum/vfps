using Microsoft.AspNetCore.Components;

namespace Vfps.Components;

public static class NavigationManagerExtensions
{
    /// <summary>
    /// Builds the "/authentication/login?returnUrl=..." URL for the current page, safe to call
    /// from any page - including ones outside the "/ui" base path (e.g. a bare "/" request,
    /// which never goes through the app's own routing/PathBase handling). NavigationManager's
    /// own ToBaseRelativePath assumes the current URI is under BaseUri and returns an empty
    /// string otherwise, and an empty (not null) returnUrl slips past `returnUrl ?? "/ui"` in
    /// the login endpoint, sending OIDC an empty RedirectUri that never completes sign-in -
    /// manifesting as an infinite login/redirect loop with a fresh Keycloak request_uri each
    /// time. This guards the same case at the source instead.
    /// </summary>
    public static string BuildLoginUrl(this NavigationManager navigationManager)
    {
        var baseUri = new Uri(navigationManager.BaseUri);
        var currentUri = new Uri(navigationManager.Uri);

        var returnUrl = baseUri.IsBaseOf(currentUri)
            ? navigationManager.ToBaseRelativePath(navigationManager.Uri)
            : string.Empty;

        return $"/ui/authentication/login?returnUrl={Uri.EscapeDataString(returnUrl)}";
    }
}
