using FujiyNotepad.Core;

namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Splits one source line into wrapped display rows for an optional word-wrap view (issue #31). Wrapping is
    /// by display column (so tabs and double-width characters count correctly) and by character — a row is the
    /// longest run of characters whose display width fits <c>wrapColumns</c>, with at least one character per
    /// row so progress is always made (a lone character wider than the viewport still gets its own row).
    ///
    /// Pure and device-free: the engine feeds it a <see cref="LineColumns"/> (already tab-expanded) and a
    /// column budget derived from the viewport width, and it returns char-index ranges. Headlessly unit-tested.
    /// </summary>
    public static class LineWrapper
    {
        /// <summary>A wrapped row: the source-char range <c>[StartChar, EndChar)</c> and the display column its
        /// first character sits at (so the renderer can map columns to x within the row).</summary>
        public readonly struct WrapRow
        {
            public WrapRow(int startChar, int endChar, int startColumn)
            {
                StartChar = startChar;
                EndChar = endChar;
                StartColumn = startColumn;
            }

            public int StartChar { get; }
            public int EndChar { get; }
            public int StartColumn { get; }
        }

        /// <summary>
        /// The wrapped rows for <paramref name="line"/> at the given column budget. Always returns at least one
        /// row (an empty line yields a single empty row). When <paramref name="wrapColumns"/> is non-positive or
        /// the line fits, a single full-line row is returned.
        /// </summary>
        public static IReadOnlyList<WrapRow> Wrap(LineColumns line, int wrapColumns)
        {
            int length = line.Source.Length;
            if (wrapColumns < 1 || length == 0 || line.TotalColumns <= wrapColumns)
            {
                return new[] { new WrapRow(0, length, 0) };
            }

            var rows = new List<WrapRow>();
            int rowStart = 0;
            int rowStartColumn = 0;

            for (int i = 0; i < length; i++)
            {
                // Column just past character i (its right edge). A char fits if its right edge stays within the
                // row's budget measured from the row's start column.
                int rightEdge = line.ColumnOfCharIndex(i + 1);
                if (rightEdge - rowStartColumn > wrapColumns && i > rowStart)
                {
                    rows.Add(new WrapRow(rowStart, i, rowStartColumn));
                    rowStart = i;
                    rowStartColumn = line.ColumnOfCharIndex(i);
                    // Re-check the current character against the fresh row (handles a char wider than the budget:
                    // it still takes its own row, then we continue).
                    rightEdge = line.ColumnOfCharIndex(i + 1);
                    if (rightEdge - rowStartColumn > wrapColumns)
                    {
                        rows.Add(new WrapRow(rowStart, i + 1, rowStartColumn));
                        rowStart = i + 1;
                        rowStartColumn = line.ColumnOfCharIndex(i + 1);
                    }
                }
            }

            // The trailing run. Skip it only when the loop already ended exactly on a row boundary (so we don't
            // emit a blank extra row); an all-empty line still yields its single row via the early return above.
            if (rowStart < length || rows.Count == 0)
            {
                rows.Add(new WrapRow(rowStart, length, rowStartColumn));
            }
            return rows;
        }

        /// <summary>The number of display rows <paramref name="line"/> wraps to (always &gt;= 1).</summary>
        public static int RowCount(LineColumns line, int wrapColumns) => Wrap(line, wrapColumns).Count;

        /// <summary>
        /// The index of the wrapped row that contains caret char position <paramref name="charIndex"/>. A caret
        /// at a row boundary belongs to the later row (where it visually sits at column 0), except end-of-line
        /// which stays on the last row.
        /// </summary>
        public static int RowOfCharIndex(IReadOnlyList<WrapRow> rows, int charIndex)
        {
            for (int r = 0; r < rows.Count; r++)
            {
                WrapRow row = rows[r];
                bool isLast = r == rows.Count - 1;
                if (charIndex < row.EndChar || (isLast && charIndex <= row.EndChar))
                {
                    if (charIndex >= row.StartChar)
                    {
                        return r;
                    }
                }
            }
            return rows.Count - 1;
        }
    }
}
