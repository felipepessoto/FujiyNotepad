namespace FujiyNotepad.Core
{
    /// <summary>
    /// An <see cref="ILineSource"/> that exposes only a chosen subset of another source's lines as a
    /// contiguous sequence — the backing for the "filter / grep" view. Filtered row <c>i</c> maps to the
    /// source line <c>sourceLines[i]</c>, so the existing view/engine renders only the matching lines with no
    /// other changes (it reads lines purely through <see cref="ILineSource"/>).
    /// </summary>
    public sealed class FilteredLineSource : ILineSource
    {
        private readonly ILineSource source;
        private readonly IReadOnlyList<int> sourceLines;

        public FilteredLineSource(ILineSource source, IReadOnlyList<int> sourceLines)
        {
            this.source = source;
            this.sourceLines = sourceLines;
        }

        /// <summary>Number of matching (visible) lines.</summary>
        public int LineCount => sourceLines.Count;

        /// <summary>Decoded text of the <paramref name="index"/>-th matching line.</summary>
        public string GetLine(int index) => source.GetLine(sourceLines[index]);

        /// <summary>The 0-based source line that filtered row <paramref name="index"/> maps to.</summary>
        public int SourceLineAt(int index) => sourceLines[index];
    }
}
