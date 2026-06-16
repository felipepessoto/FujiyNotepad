namespace FujiyNotepad.WinUI.Logic
{
    /// <summary>
    /// Maps document line indices to pixel rows on the vertical scrollbar track, so the host can paint marker
    /// ticks (e.g. bookmarks, find matches) showing where they are in a large file. Dense indices collapse to at
    /// most one tick per row. Pure math with no UI dependency, so it is headlessly unit-testable.
    /// </summary>
    public static class ScrollbarMarkers
    {
        /// <summary>
        /// The distinct integer Y rows (ascending, each in <c>[0, trackHeightPx)</c>) at which to draw a tick for
        /// the given <paramref name="lines"/>, mapping line <c>0</c> to the top row and line
        /// <c>totalLines - 1</c> to the bottom row. Returns an empty list when there is nothing to map or the
        /// track has no size. Out-of-range line indices are clamped into the document.
        /// </summary>
        public static IReadOnlyList<int> Rows(IEnumerable<int> lines, int totalLines, double trackHeightPx)
        {
            int height = (int)trackHeightPx;
            if (lines is null || totalLines <= 0 || height <= 0)
            {
                return Array.Empty<int>();
            }

            var rows = new SortedSet<int>();
            foreach (int line in lines)
            {
                rows.Add(RowOf(line, totalLines, height));
            }

            return rows.Count == 0 ? Array.Empty<int>() : new List<int>(rows);
        }

        /// <summary>
        /// Maps a single line index to its pixel row in <c>[0, height)</c> (line <c>0</c> -> top, line
        /// <c>totalLines - 1</c> -> bottom), clamping out-of-range indices. Returns 0 for a non-positive track or
        /// total. Also used to pre-bucket a huge match set into a fixed-resolution space.
        /// </summary>
        public static int RowOf(int line, int totalLines, int height)
        {
            if (totalLines <= 0 || height <= 0)
            {
                return 0;
            }

            int lastLine = totalLines - 1;
            int lastRow = height - 1;
            int clamped = line < 0 ? 0 : (line > lastLine ? lastLine : line);
            return lastLine == 0 ? 0 : (int)Math.Round((double)clamped / lastLine * lastRow);
        }
    }
}
