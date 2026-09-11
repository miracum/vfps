using System.Reflection;
using Microsoft.AspNetCore.Components;
using Vfps.Components;

namespace Vfps.Tests.ComponentsTests;

public class NavigationManagerExtensionsTests
{
    // NavigationManager is abstract, and every supported way to set Uri/BaseUri on it - the
    // public Initialize(baseUri, uri), and even the protected Uri/BaseUri setters directly -
    // validates that uri is actually under baseUri, throwing before the "outside base" case
    // below (the one BuildLoginUrl's own doc comment calls out as the reason it exists, guarding
    // a real past incident: an infinite OIDC login/redirect loop) could ever be constructed
    // through them. Reflection into the private backing fields is the only way to build that
    // state for a test - a real limitation of NavigationManager as a test double, not a design
    // choice made lightly.
    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string baseUri, string uri)
        {
            var type = typeof(NavigationManager);
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            type.GetField("_baseUri", flags)!.SetValue(this, new Uri(baseUri));
            type.GetField("_uri", flags)!.SetValue(this, uri);
            type.GetField("_isInitialized", flags)!.SetValue(this, true);
        }
    }

    [Fact]
    public void BuildLoginUrl_WithUriInsideBase_ShouldReturnAnAbsolutePathOnThisSite()
    {
        var navigationManager = new TestNavigationManager(
            "http://localhost/ui/",
            "http://localhost/ui/namespaces"
        );

        var url = navigationManager.BuildLoginUrl();

        // "/ui/namespaces", not the bare "namespaces" ToBaseRelativePath hands back: relative
        // would resolve against the OIDC callback's own path rather than the UI's base.
        url.Should().Be("/ui/authentication/login?returnUrl=%2Fui%2Fnamespaces");
    }

    [Fact]
    public void BuildLoginUrl_WithUriOutsideBase_ShouldFallBackToTheUiRoot()
    {
        // e.g. a bare "/" request, which never goes through the app's own "/ui" PathBase
        // handling - see the method's own doc comment on why this must not blindly delegate to
        // NavigationManager.ToBaseRelativePath (which would otherwise send OIDC an empty
        // RedirectUri and loop forever).
        var navigationManager = new TestNavigationManager(
            "http://localhost/ui/",
            "http://localhost/"
        );

        var url = navigationManager.BuildLoginUrl();

        url.Should().Be("/ui/authentication/login?returnUrl=%2Fui");
    }

    // The two halves of the login redirect have to agree: the login endpoint only honours
    // absolute local paths (see ReturnUrl), so a returnUrl built here that it would reject would
    // send every UI-initiated login back to the home page instead of the page it started on -
    // silently, and only once signed in.
    [Theory]
    [InlineData("http://localhost/ui/namespaces", "/ui/namespaces")]
    [InlineData("http://localhost/ui/", "/ui")]
    [InlineData("http://localhost/", "/ui")]
    public void BuildLoginUrl_ShouldProduceAReturnUrlTheLoginEndpointHonours(
        string currentUri,
        string expected
    )
    {
        var navigationManager = new TestNavigationManager("http://localhost/ui/", currentUri);

        var url = navigationManager.BuildLoginUrl();
        var returnUrl = Uri.UnescapeDataString(url.Split("returnUrl=")[1]);

        ReturnUrl.Resolve(returnUrl).Should().Be(expected);
    }
}
