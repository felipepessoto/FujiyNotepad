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

            int lastLine = totalLines - 1;
            int lastRow = height - 1;
            var rows = new SortedSet<int>();

            foreach (int line in lines)
            {
                int clamped = line < 0 ? 0 : (line > lastLine ? lastLine : line);
                int row = lastLine == 0 ? 0 : (int)Math.Round((double)clamped / lastLine * lastRow);
                rows.Add(row);
            }

            return rows.Count == 0 ? Array.Empty<int>() : new List<int>(rows);
        }
    }
}
