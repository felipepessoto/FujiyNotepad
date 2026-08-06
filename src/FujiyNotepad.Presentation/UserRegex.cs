using System.Text.RegularExpressions;

namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Builds the <see cref="Regex"/> objects for patterns the <b>user</b> typed — the Find bar, the Filter bar
    /// and the persistent highlight rules — with a per-match timeout so a pattern with catastrophic backtracking
    /// (the classic <c>(a+)+$</c> against a long non-matching line) cannot hang the app.
    ///
    /// <para>The timeout is a per-<em>match</em> budget, not a per-file one: it bounds the work a single line can
    /// cost, which is what makes it safe to run these patterns against every visible line on the render path.</para>
    ///
    /// <para>Native-AOT safety is unchanged. The interpreted engine is still used (never
    /// <see cref="RegexOptions.Compiled"/>, which emits IL at runtime), and a match timeout needs no codegen.
    /// <see cref="RegexOptions.NonBacktracking"/> would remove the risk at the source but cannot be a blanket
    /// switch here — it rejects backreferences and lookarounds, which users legitimately type.</para>
    /// </summary>
    public static class UserRegex
    {
        /// <summary>
        /// How long a single line's match may run before it is abandoned. Generous for any sane pattern on a
        /// real log line, while still bounding a pathological one to something a person reads as a hiccup.
        /// </summary>
        public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// A <see cref="Regex"/> over a user-supplied <paramref name="pattern"/>, carrying the shared
        /// <see cref="MatchTimeout"/>. Throws <see cref="ArgumentException"/> for a malformed pattern, exactly
        /// as the plain constructor does, so existing "Invalid regex" handling is unaffected.
        /// <para><see cref="RegexOptions.Compiled"/> is stripped rather than trusted: it emits IL at runtime,
        /// which a Native-AOT build cannot do. Enforcing it here means the invariant holds for every present and
        /// future caller instead of relying on each one remembering.</para>
        /// </summary>
        public static Regex Create(string pattern, RegexOptions options)
            => new Regex(pattern, options & ~RegexOptions.Compiled, MatchTimeout);
    }
}
