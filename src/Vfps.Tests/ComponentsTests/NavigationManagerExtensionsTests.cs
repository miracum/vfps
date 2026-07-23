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
    public void BuildLoginUrl_WithUriInsideBase_ShouldIncludeReturnUrl()
    {
        var navigationManager = new TestNavigationManager(
            "http://localhost/ui/",
            "http://localhost/ui/namespaces"
        );

        var url = navigationManager.BuildLoginUrl();

        url.Should().StartWith("/ui/authentication/login?returnUrl=");
        url.Should().NotBe("/ui/authentication/login?returnUrl=");
    }

    [Fact]
    public void BuildLoginUrl_WithUriOutsideBase_ShouldReturnEmptyReturnUrl()
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

        url.Should().Be("/ui/authentication/login?returnUrl=");
    }
}
