namespace FujiyNotepad.WinUI.Logic
{
    /// <summary>
    /// Find state for repeated forward regex "find next", expressed in (line, character) coordinates.
    /// It mirrors <see cref="FindController"/> (which works in byte offsets for the literal search): each
    /// search resumes one character past the previous match, and it restarts from the caret when the
    /// pattern changes or after a search that found nothing. Pure state with no I/O, so it unit-tests on a
    /// normal .NET test host.
    /// </summary>
    public sealed class RegexFindController
    {
        private string? pattern;
        private int lastLine = -1;
        private int lastChar = -1;
        private int lastLen;

        /// <summary>True once a match has been recorded for the current pattern.</summary>
        public bool HasMatch => lastLine >= 0;

        /// <summary>
        /// Prepares a forward "find next" for <paramref name="newPattern"/> and returns the (line, char)
        /// to start scanning from. A new pattern (or one with no recorded match yet) starts at the caret;
        /// otherwise the search resumes one character past the last match.
        /// </summary>
        public (int Line, int Char) PrepareForwardSearch(string newPattern, int caretLine, int caretChar)
        {
            if (!string.Equals(newPattern, pattern, StringComparison.Ordinal))
            {
                pattern = newPattern;
                lastLine = -1;
                lastChar = -1;
            }
            // Resume past the end of the last match (not one char past its start) so matches never overlap.
            return lastLine >= 0 ? (lastLine, lastChar + Math.Max(1, lastLen)) : (caretLine, caretChar);
        }

        /// <summary>Records a found match (line, start column, character length) so the next search resumes past its end.</summary>
        public void RecordMatch(int line, int charColumn, int length)
        {
            lastLine = line;
            lastChar = charColumn;
            lastLen = length;
        }

        /// <summary>Records that no match was found, so the next search restarts from the caret.</summary>
        public void RecordNoMatch()
        {
            lastLine = -1;
            lastChar = -1;
        }

        /// <summary>Clears all state (e.g. when the file is closed/switched or an option changes).</summary>
        public void Reset()
        {
            pattern = null;
            lastLine = -1;
            lastChar = -1;
            lastLen = 0;
        }
    }
}
