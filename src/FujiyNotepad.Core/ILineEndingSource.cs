namespace FujiyNotepad.Core
{
    /// <summary>
    /// Optional capability for an <see cref="ILineSource"/> that can report each line's terminator
    /// (<see cref="LineEnding.Lf"/>, <see cref="LineEnding.CrLf"/>, or <see cref="LineEnding.None"/> for an
    /// unterminated final line). Used to draw per-line CR/LF glyphs in the "Show Whitespace" view; the line
    /// text itself is delivered without its terminator, so this exposes it separately.
    /// </summary>
    public interface ILineEndingSource
    {
        /// <summary>The terminator of line <paramref name="lineIndex"/> (0-based): CrLf, Lf, or None.</summary>
        LineEnding GetLineEnding(int lineIndex);
    }
}
