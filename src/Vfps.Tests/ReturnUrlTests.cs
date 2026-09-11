namespace Vfps.Tests;

/// <summary>
/// The login endpoint is anonymous and its returnUrl is followed after a successful sign-in, so
/// it must never be able to point off-site - see <see cref="ReturnUrl"/>.
/// </summary>
public class ReturnUrlTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/ui")]
    [InlineData("/hangfire")]
    [InlineData("/ui/namespaces")]
    [InlineData("/ui/namespaces?filter=a&page=2")]
    [InlineData("/ui/namespaces#section")]
    public void Resolve_AnAbsolutePathOnThisSite_ShouldKeepIt(string returnUrl)
    {
        ReturnUrl.Resolve(returnUrl).Should().Be(returnUrl);
    }

    [Theory]
    // Protocol-relative, and the backslash spelling browsers accept just as readily.
    [InlineData("//evil.example")]
    [InlineData("//evil.example/ui/namespaces")]
    [InlineData("/\\evil.example")]
    [InlineData("/\\\\evil.example")]
    // Absolute URLs, in the spellings that get past a naive "does it start with http" check.
    [InlineData("https://evil.example")]
    [InlineData("HTTPS://evil.example")]
    [InlineData("//evil.example\\@vfps.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("javascript:alert(1)")]
    // A browser strips these before resolving, turning the first into "//evil.example".
    [InlineData("/\t/evil.example")]
    [InlineData("/\n/evil.example")]
    [InlineData("/\r/evil.example")]
    // Relative paths are no longer produced by any caller, so they are not honoured either.
    [InlineData("namespaces")]
    [InlineData("")]
    [InlineData(null)]
    public void Resolve_AnythingThatCouldLeaveThisSite_ShouldFallBackToTheUi(string? returnUrl)
    {
        ReturnUrl.Resolve(returnUrl).Should().Be(ReturnUrl.Fallback);
    }

    [Fact]
    public void Resolve_TheFallback_ShouldItselfBeALocalPath()
    {
        // Guards the constant against an edit that would make every rejected value an open
        // redirect in its own right.
        ReturnUrl.Resolve(ReturnUrl.Fallback).Should().Be(ReturnUrl.Fallback);
    }
}
