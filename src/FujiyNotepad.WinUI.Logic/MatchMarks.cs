namespace FujiyNotepad.WinUI.Logic
{
    /// <summary>
    /// Accumulates find-match positions as a small, fixed-resolution set of buckets, so the scrollbar marker
    /// margin can show where matches are even for a file with millions of matches, without retaining every
    /// position. A bucket is <see cref="ScrollbarMarkers.RowOf"/> of the line in a <see cref="Resolution"/>-tall
    /// space; the margin then maps the buckets to the live track height. Constant memory (at most
    /// <see cref="Resolution"/> buckets); built during the background match scan and read on the UI thread.
    /// </summary>
    public sealed class MatchMarks
    {
        /// <summary>Number of vertical buckets matches collapse into (ample for any real scrollbar height).</summary>
        public const int Resolution = 2048;

        private readonly int totalLines;
        private readonly HashSet<int> buckets = new();

        public MatchMarks(int totalLines) => this.totalLines = totalLines;

        /// <summary>True once every bucket is filled, so the caller can stop mapping further matches.</summary>
        public bool IsFull => buckets.Count >= Resolution;

        /// <summary>Number of distinct buckets recorded.</summary>
        public int Count => buckets.Count;

        /// <summary>The distinct bucket positions; map to track rows via <c>ScrollbarMarkers.Rows(Buckets, Resolution, h)</c>.</summary>
        public IReadOnlyCollection<int> Buckets => buckets;

        /// <summary>Records a match on <paramref name="line"/> (a no-op once <see cref="IsFull"/>).</summary>
        public void Add(int line)
        {
            if (buckets.Count < Resolution)
            {
                buckets.Add(ScrollbarMarkers.RowOf(line, totalLines, Resolution));
            }
        }
    }
}
