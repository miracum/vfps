using System.Text.RegularExpressions;
using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <summary>
/// The single definition of what a namespace's
/// <see cref="Namespace.OriginalValueValidationRegex"/> means: whether a pattern is usable at all,
/// and whether a given original value passes it.
///
/// Shared by <see cref="PseudonymAppService"/> (which enforces it on every pseudonym created),
/// <see cref="NamespaceAppService"/> (which rejects an unusable pattern when the namespace is
/// created, rather than letting it fail lazily on every create afterwards) and the admin UI's live
/// pattern checker - so what the UI tells an admin their pattern will do is, by construction, what
/// the server actually does with it.
/// </summary>
public static class OriginalValueValidation
{
    /// <summary>
    /// A short, fixed timeout guards against a catastrophically backtracking pattern turning a
    /// single pseudonym request into a denial of service - the pattern is admin-supplied at
    /// namespace creation, not attacker-controlled, but this is cheap insurance regardless. It
    /// applies to the admin UI's own try-it-out box too, which is what lets that box warn about
    /// such a pattern *before* the namespace exists.
    /// </summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Checks whether <paramref name="pattern"/> can be used as a validation regex at all.
    /// </summary>
    /// <returns>
    /// Null when it can - including for a null/empty pattern, which simply means "no validation".
    /// Otherwise the regex parser's own explanation of what is wrong with it (e.g. "Invalid
    /// pattern '[a' at offset 2. Unterminated [] set."), which names the offending construct and
    /// its position and so is far more useful to whoever typed it than a generic "invalid".
    /// </returns>
    public static string? DescribePatternError(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        try
        {
            // Parses the pattern without running it against anything - cheap enough to call on
            // every keystroke of the admin UI's pattern field.
            _ = new Regex(pattern);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Whether <paramref name="originalValue"/> passes <paramref name="pattern"/>. Deliberately
    /// unanchored, exactly as written: a pattern matching anywhere in the value is accepted, so an
    /// admin who means "the whole value" has to say so with <c>^</c>/<c>$</c> themselves. The
    /// admin UI runs this same method against a sample value, which is what makes that surprise
    /// discoverable before the namespace is created rather than after.
    /// </summary>
    /// <exception cref="RegexMatchTimeoutException">
    /// The pattern took longer than <see cref="RegexTimeout"/> on this value - valid, but
    /// catastrophically slow on it.
    /// </exception>
    public static bool IsMatch(string pattern, string originalValue) =>
        Regex.IsMatch(originalValue, pattern, RegexOptions.None, RegexTimeout);
}
