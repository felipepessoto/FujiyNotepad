namespace FujiyNotepad.Core
{
    /// <summary>
    /// Options for the literal byte search in <see cref="TextSearcher"/>. The default value
    /// (case-sensitive, not whole-word) reproduces the original literal search, so existing callers
    /// and <c>default</c> behave identically.
    /// </summary>
    public readonly struct SearchOptions
    {
        /// <summary>
        /// Match ASCII letters case-insensitively (A-Z is folded to a-z). Non-ASCII bytes always match
        /// exactly; byte-level search can't do Unicode case folding.
        /// </summary>
        public bool IgnoreCase { get; init; }

        /// <summary>
        /// Only accept a match when the bytes immediately before and after it are word boundaries: a
        /// non-word ASCII byte (anything outside <c>[A-Za-z0-9_]</c>) or the start/end of the file.
        /// </summary>
        public bool WholeWord { get; init; }

        /// <summary>
        /// Code-unit size for multi-byte encodings: when greater than 1, a match is only accepted at a file
        /// offset that is a multiple of this, so it lands on a character boundary (e.g. 2 for UTF-16, 4 for
        /// UTF-32). The default (0/1) accepts matches at any byte offset — the original single-byte behaviour.
        /// </summary>
        public int UnitAlignment { get; init; }
    }
}
