using System.Text.RegularExpressions;

namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Builds the <see cref="Regex"/> used by the Find bar's regex mode from the user's term and the
    /// match-case / whole-word toggles. Whole-word wraps the term in <c>\b(?:…)\b</c> so the user's pattern
    /// still groups correctly, and match-case off adds <see cref="RegexOptions.IgnoreCase"/>. Always
    /// <see cref="RegexOptions.CultureInvariant"/>, and interpreted (no compilation) so it stays Native-AOT
    /// safe. Carries <see cref="UserRegex.MatchTimeout"/> so a catastrophically backtracking term cannot hang
    /// the search. A malformed term throws <see cref="ArgumentException"/>, which the caller surfaces as
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
            return UserRegex.Create(pattern, options);
        }

        /// <summary>
        /// The regex for a <b>literal</b> whole-word match. Deliberately does <em>not</em> use <c>\b</c>: .NET's
        /// <c>\w</c> is Unicode-aware, so a letter from any script counts as a word character, whereas the byte
        /// scanner that backs the same feature (<c>TextSearcher</c>'s whole-word check) uses an ASCII-only
        /// definition. With <c>\b</c> the two disagree — e.g. <c>ERROR</c> between two CJK ideographs is a
        /// whole-word match to the scanner but not to the regex — so the Filter would return different lines
        /// depending on whether its byte-scan fast path or its per-line decode path ran, which is decided by
        /// something the user cannot see (whether indexing has finished). Mirroring the ASCII definition here
        /// keeps the two paths in agreement.
        /// </summary>
        public static Regex BuildLiteralWholeWord(string text, bool matchCase)
        {
            RegexOptions options = RegexOptions.CultureInvariant;
            if (!matchCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            return UserRegex.Create($@"(?<![A-Za-z0-9_]){Regex.Escape(text)}(?![A-Za-z0-9_])", options);
        }
    }
}
