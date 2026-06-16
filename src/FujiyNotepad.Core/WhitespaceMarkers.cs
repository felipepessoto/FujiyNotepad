namespace FujiyNotepad.Core
{
    /// <summary>The kind of whitespace/control glyph to draw at a marker position.</summary>
    public enum WhitespaceKind
    {
        /// <summary>An ASCII space (U+0020) - drawn as a small dot.</summary>
        Space,

        /// <summary>A tab (U+0009) - drawn as an arrow spanning its expanded columns.</summary>
        Tab,

        /// <summary>A C0/C1 control character other than tab - drawn as a small box.</summary>
        Control,

        /// <summary>A line terminated by a bare <c>\n</c> - drawn as a return mark at the line's end.</summary>
        Lf,

        /// <summary>A line terminated by <c>\r\n</c> - drawn as a return mark (with a CR tick) at the line's end.</summary>
        CrLf,
    }

    /// <summary>
    /// A whitespace/control glyph to overlay on a rendered line: its display <see cref="Column"/> (and
    /// <see cref="Width"/> in columns, &gt;1 only for tabs), its <see cref="Kind"/>, and whether it is part of
    /// the line's <see cref="Trailing"/> whitespace run (so the renderer can emphasize it).
    /// </summary>
    public readonly record struct WhitespaceMarker(int Column, int Width, WhitespaceKind Kind, bool Trailing);

    /// <summary>
    /// Computes the whitespace/control markers to draw for a line when "Show Whitespace" is on: a dot per space,
    /// an arrow per tab, and a box per other control character, with the line's trailing space/tab run flagged.
    /// Pure (works off the line's <see cref="LineColumns"/> column map), so it is headlessly unit-testable.
    /// </summary>
    public static class WhitespaceMarkers
    {
        private static readonly IReadOnlyList<WhitespaceMarker> None = Array.Empty<WhitespaceMarker>();

        /// <summary>
        /// The markers for <paramref name="columns"/>, in source order. Spaces and tabs after the last
        /// non-space/non-tab character are flagged <see cref="WhitespaceMarker.Trailing"/> (an all-blank line is
        /// entirely trailing). When <paramref name="ending"/> is <see cref="LineEnding.Lf"/> or
        /// <see cref="LineEnding.CrLf"/>, a final marker of that kind is appended at the line's end column.
        /// Returns an empty list when the line has no whitespace, control characters, or terminator to mark.
        /// </summary>
        public static IReadOnlyList<WhitespaceMarker> Compute(LineColumns columns, LineEnding ending = LineEnding.None)
        {
            string source = columns.Source;
            int n = source.Length;

            // Index of the last character that is neither a space nor a tab; everything after it is the
            // trailing whitespace run (-1 when the whole line is spaces/tabs).
            int lastSignificant = -1;
            for (int i = 0; i < n; i++)
            {
                char c = source[i];
                if (c != ' ' && c != '\t')
                {
                    lastSignificant = i;
                }
            }

            List<WhitespaceMarker>? markers = null;
            for (int i = 0; i < n; i++)
            {
                char c = source[i];
                int column = columns.ColumnOfCharIndex(i);

                if (c == ' ')
                {
                    (markers ??= new()).Add(new WhitespaceMarker(column, 1, WhitespaceKind.Space, i > lastSignificant));
                }
                else if (c == '\t')
                {
                    int width = columns.ColumnOfCharIndex(i + 1) - column;
                    (markers ??= new()).Add(new WhitespaceMarker(column, width, WhitespaceKind.Tab, i > lastSignificant));
                }
                else if (char.IsControl(c))
                {
                    (markers ??= new()).Add(new WhitespaceMarker(column, 1, WhitespaceKind.Control, false));
                }
            }

            // A return mark at the end of the line for its terminator (None for the final unterminated line).
            if (ending == LineEnding.Lf || ending == LineEnding.CrLf)
            {
                WhitespaceKind kind = ending == LineEnding.CrLf ? WhitespaceKind.CrLf : WhitespaceKind.Lf;
                (markers ??= new()).Add(new WhitespaceMarker(columns.TotalColumns, 1, kind, false));
            }

            return (IReadOnlyList<WhitespaceMarker>?)markers ?? None;
        }
    }
}
