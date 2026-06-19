namespace FujiyNotepad.Presentation
{
    /// <summary>How a tailed file's byte length changed since the previous observation.</summary>
    public enum TailChange
    {
        None,
        Grew,
        Shrunk,
    }

    /// <summary>
    /// Pure decision logic for tail / follow mode (issue #28): turns successive observed file lengths into a
    /// grow / shrink / unchanged decision, and decides when a sticky-bottom follow should snap to the new end.
    /// Device-free and unit-tested; the WinUI layer owns the poll timer, the resume-index and the scroll.
    /// </summary>
    public sealed class TailController
    {
        private long lastLength;

        public TailController(long initialLength) => lastLength = initialLength;

        /// <summary>The most recently observed length.</summary>
        public long LastLength => lastLength;

        /// <summary>
        /// Records <paramref name="currentLength"/> and reports how it changed: <see cref="TailChange.Grew"/>
        /// when the file got longer (new content to index), <see cref="TailChange.Shrunk"/> when it got shorter
        /// (truncation / rotation — reset), or <see cref="TailChange.None"/> when unchanged.
        /// </summary>
        public TailChange Observe(long currentLength)
        {
            if (currentLength > lastLength)
            {
                lastLength = currentLength;
                return TailChange.Grew;
            }
            if (currentLength < lastLength)
            {
                lastLength = currentLength;
                return TailChange.Shrunk;
            }
            return TailChange.None;
        }

        /// <summary>
        /// True when a sticky-bottom follow should snap to the new end: only when the viewport is already at the
        /// bottom, so a user who scrolled up to read history is left where they are.
        /// </summary>
        public static bool ShouldStickToBottom(int firstVisibleLine, int maxFirstLine) =>
            firstVisibleLine >= maxFirstLine;
    }
}
