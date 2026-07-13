using System.Security.Claims;
using Vfps.Authorization;

namespace Vfps.Tests.AuthorizationTests;

public class ClaimsPrincipalExtensionsTests
{
    [Fact]
    public void GetSubject_WithSubClaim_ShouldReturnItsValue()
    {
        // "sub", not ClaimTypes.NameIdentifier - matches what a real OIDC/JWT principal
        // actually contains once Program.cs's MapInboundClaims = false takes effect.
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-123")]));

        user.GetSubject().Should().Be("user-123");
    }

    [Fact]
    public void GetSubject_WithoutSubClaim_ShouldReturnAnonymous()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("roles", "some-role")]));

        user.GetSubject().Should().Be("anonymous");
    }

    [Fact]
    public void GetSubject_WithOnlyLegacyNameIdentifierClaim_ShouldReturnAnonymous()
    {
        // Regression guard: a claim typed ClaimTypes.NameIdentifier (the long XML/SOAP URI) is
        // what the .NET JWT stack would auto-map "sub" to by default - but this app disables
        // that mapping, so a claim of only that type must NOT be picked up here.
        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-123")])
        );

        user.GetSubject().Should().Be("anonymous");
    }
}
