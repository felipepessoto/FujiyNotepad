using System.Text.RegularExpressions;

namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Builds the <see cref="Regex"/> used by the Find bar's regex mode from the user's term and the
    /// match-case / whole-word toggles. Whole-word wraps the term in <c>\b(?:…)\b</c> so the user's pattern
    /// still groups correctly, and match-case off adds <see cref="RegexOptions.IgnoreCase"/>. Always
    /// <see cref="RegexOptions.CultureInvariant"/>, and interpreted (no compilation) so it stays Native-AOT
    /// safe. A malformed term throws <see cref="ArgumentException"/>, which the caller surfaces as
    /// "Invalid regex". Pure and unit-testable.
    /// </summary>
    public static class FindRegexBuilder
    {
        public static Regex Build(string text, bool matchCase, bool wholeWord)
        {
            RegexOptions options = RegexOptions.CultureInvariant;
            if (!matchCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            string pattern = wholeWord ? $@"\b(?:{text})\b" : text;
            return new Regex(pattern, options);
        }
    }
}
