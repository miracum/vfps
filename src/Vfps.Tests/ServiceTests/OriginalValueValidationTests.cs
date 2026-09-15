using System.Text.RegularExpressions;
using Vfps.AppServices;

namespace Vfps.Tests.ServiceTests;

// The one place a namespace's OriginalValueValidationRegex is interpreted - by the create path
// that enforces it, the namespace create path that rejects an unusable pattern, and the admin
// UI's live checker alike. Tested directly because the UI's whole promise is that its verdict is
// the same one the server will reach.
public class OriginalValueValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DescribePatternError_WithNoPattern_ShouldReportNoError(string? pattern)
    {
        // No pattern means no validation - the default, not a mistake.
        OriginalValueValidation.DescribePatternError(pattern).Should().BeNull();
    }

    [Theory]
    [InlineData("^[0-9]+$")]
    [InlineData(".*")]
    [InlineData(@"^[A-Z]{2}\d{6}$")]
    public void DescribePatternError_WithAUsablePattern_ShouldReportNoError(string pattern)
    {
        OriginalValueValidation.DescribePatternError(pattern).Should().BeNull();
    }

    [Theory]
    [InlineData("(unterminated")]
    [InlineData("[a")]
    [InlineData("*")]
    [InlineData(@"(?<")]
    public void DescribePatternError_WithAnUnusablePattern_ShouldExplainWhy(string pattern)
    {
        var error = OriginalValueValidation.DescribePatternError(pattern);

        // The regex parser's own explanation, not a generic "invalid" - it names the offending
        // construct and where it is, which is the whole reason it's surfaced verbatim in the UI.
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("^[0-9]+$", "12345", true)]
    [InlineData("^[0-9]+$", "12a45", false)]
    // Unanchored patterns match a substring. This is the surprise the admin UI's try-it-out box
    // exists to make visible before a namespace is created around it.
    [InlineData("[0-9]+", "abc123", true)]
    [InlineData("^[0-9]+$", "", false)]
    public void IsMatch_ShouldApplyThePatternExactlyAsWritten(
        string pattern,
        string originalValue,
        bool expected
    )
    {
        OriginalValueValidation.IsMatch(pattern, originalValue).Should().Be(expected);
    }

    [Fact]
    public void IsMatch_WithACatastrophicallyBacktrackingPattern_ShouldTimeOutRatherThanHang()
    {
        // A pattern that compiles fine and still runs effectively forever on the wrong input.
        // Without the timeout this would take exponential time; with it, every caller - a
        // pseudonym create and the admin UI's checker alike - gets a prompt, reportable failure.
        var act = () => OriginalValueValidation.IsMatch("^(a+)+$", new string('a', 40) + "!");

        act.Should().Throw<RegexMatchTimeoutException>();
    }
}
