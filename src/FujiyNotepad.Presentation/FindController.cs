namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Tracks Find state for repeated forward "find next" searches: it remembers the active term and the
    /// last match offset so each search resumes past the previous hit. It restarts from the caret when the
    /// term changes or after a search that found nothing (so the next search wraps back to the caret).
    /// This is pure state with no I/O, so it unit-tests on a normal .NET test host.
    /// </summary>
    public sealed class FindController
    {
        private string? term;
        private long lastMatchOffset = -1;
        private int lastMatchLength;

        /// <summary>The term of the most recent search, or <c>null</c> if none.</summary>
        public string? Term => term;

        /// <summary>True once a match has been recorded for the current term.</summary>
        public bool HasMatch => lastMatchOffset >= 0;

        /// <summary>
        /// Prepares a forward "find next" for <paramref name="newTerm"/> and returns the byte offset to
        /// start scanning from. A new term (or one with no recorded match yet) starts at
        /// <paramref name="caretAnchorOffset"/>; otherwise the search resumes one byte past the last match.
        /// </summary>
        public long PrepareForwardSearch(string newTerm, long caretAnchorOffset)
        {
            if (!string.Equals(newTerm, term, StringComparison.Ordinal))
            {
                term = newTerm;
                lastMatchOffset = -1;
            }
            // Resume past the end of the last match (not one byte past its start) so matches never overlap.
            return lastMatchOffset >= 0 ? lastMatchOffset + Math.Max(1, lastMatchLength) : caretAnchorOffset;
        }

        /// <summary>
        /// Records a found match (its start offset and byte length) so the next search resumes past its end.
        /// </summary>
        public void RecordMatch(long offset, int length)
        {
            lastMatchOffset = offset;
            lastMatchLength = length;
        }

        /// <summary>Records that no match was found, so the next search restarts from the caret anchor.</summary>
        public void RecordNoMatch() => lastMatchOffset = -1;

        /// <summary>Clears all state (e.g. when the file is closed or switched).</summary>
        public void Reset()
        {
            term = null;
            lastMatchOffset = -1;
            lastMatchLength = 0;
        }
    }
}
