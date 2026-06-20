namespace FujiyNotepad.Core
{
    /// <summary>
    /// Read access to decoded file lines by index, for line-oriented features such as regex Find.
    /// <see cref="LineProvider"/> is the production implementation; tests can supply a simple fake.
    /// </summary>
    public interface ILineSource
    {
        /// <summary>Number of lines currently available (only indexed lines while indexing is in progress).</summary>
        int LineCount { get; }

        /// <summary>Decoded text of line <paramref name="lineIndex"/> (0-based), without its terminator.</summary>
        string GetLine(int lineIndex);

        /// <summary>
        /// Decoded text of line <paramref name="lineIndex"/> for a one-pass bulk scan (filter / export), which
        /// must not pollute the line cache: a full-file scan reads each line once, so caching every line only
        /// thrashes the bounded cache and evicts the hot viewport lines for no reuse. The default simply calls
        /// <see cref="GetLine"/>; <see cref="LineProvider"/> overrides it to read without inserting into its cache.
        /// </summary>
        string GetLineUncached(int lineIndex) => GetLine(lineIndex);
    }
}
