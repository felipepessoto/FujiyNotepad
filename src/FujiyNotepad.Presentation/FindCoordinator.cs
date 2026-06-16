namespace FujiyNotepad.Presentation
{
    /// <summary>The direction a Find run scans the document.</summary>
    public enum FindDirection
    {
        Forward,
        Backward,
    }

    /// <summary>
    /// Owns the cross-cutting Find state machine that decides where each "find next"/"find previous" starts
    /// and how the wrap/caret state evolves after each result — the logic that previously lived inline (and
    /// untested) in <c>MainWindow</c>, where the last several Find bug fixes were made. It coordinates the two
    /// coordinate-specific resume controllers (<see cref="FindController"/> for the literal byte search and
    /// <see cref="RegexFindController"/> for the line-scoped regex search) with the shared state:
    /// <list type="bullet">
    /// <item>the active find <em>key</em> (term + options) — a change starts a fresh search,</item>
    /// <item>the caret the last result left behind — a different caret means the user moved it, so the next
    /// search restarts from the caret instead of resuming,</item>
    /// <item>the two mutually-exclusive wrap flags — a forward miss wraps to the document start next time, a
    /// backward miss wraps to the end.</item>
    /// </list>
    /// It is pure state with no I/O, so it unit-tests on a normal .NET host. The caller supplies the concrete
    /// anchors (byte offset / line+column read from the view and index) and performs the actual search.
    /// </summary>
    public sealed class FindCoordinator
    {
        private readonly FindController literal = new();
        private readonly RegexFindController regex = new();

        private string? lastKey;
        private TextPosition? lastCaret;
        private bool forwardWrapPending;   // a forward miss armed a wrap to the start of the document
        private bool backwardWrapPending;  // a backward miss armed a wrap to the end of the document

        /// <summary>True when the next forward search will wrap to the start of the document.</summary>
        public bool ForwardWrapPending => forwardWrapPending;

        /// <summary>True when the next backward search will wrap to the end of the document.</summary>
        public bool BackwardWrapPending => backwardWrapPending;

        /// <summary>
        /// Called at the start of every Find run. A changed <paramref name="key"/> (term or options) is a fresh
        /// search, so any pending wrap is dropped. A <paramref name="currentCaret"/> that differs from where the
        /// last result left the caret means the user moved it, so the resume controllers are reset to restart
        /// from the caret and any pending wrap is dropped.
        /// </summary>
        public void Begin(string key, TextPosition currentCaret)
        {
            if (!string.Equals(key, lastKey, StringComparison.Ordinal))
            {
                forwardWrapPending = false;
                backwardWrapPending = false;
                lastKey = key;
            }

            if (lastCaret is { } previous && !currentCaret.Equals(previous))
            {
                literal.RecordNoMatch();
                regex.RecordNoMatch();
                forwardWrapPending = false;
                backwardWrapPending = false;
                lastCaret = null;
            }
        }

        /// <summary>
        /// The byte offset a forward literal search should start scanning from: the start of the document when a
        /// forward wrap is pending, otherwise <paramref name="caretOffset"/>, resolved through the resume
        /// controller (which continues past the previous match for the same <paramref name="term"/>). Consumes
        /// the pending forward wrap.
        /// </summary>
        public long PlanLiteralForward(string term, long caretOffset)
        {
            long anchor = forwardWrapPending ? 0 : caretOffset;
            forwardWrapPending = false;
            return literal.PrepareForwardSearch(term, anchor);
        }

        /// <summary>
        /// The exclusive upper-bound byte offset a backward literal search should stop before:
        /// <see cref="long.MaxValue"/> (clamped by the searcher to the document end) when a backward wrap is
        /// pending, otherwise <paramref name="selectionStartOffset"/>. Consumes the pending backward wrap.
        /// </summary>
        public long PlanLiteralBackward(long selectionStartOffset)
        {
            long before = backwardWrapPending ? long.MaxValue : selectionStartOffset;
            backwardWrapPending = false;
            return before;
        }

        /// <summary>
        /// The (line, char) a forward regex search should start from: the document start when a forward wrap is
        /// pending, otherwise <paramref name="caret"/>, resolved through the resume controller. Consumes the
        /// pending forward wrap.
        /// </summary>
        public (int Line, int Char) PlanRegexForward(string pattern, TextPosition caret)
        {
            int line = forwardWrapPending ? 0 : caret.Line;
            int column = forwardWrapPending ? 0 : caret.Column;
            forwardWrapPending = false;
            return regex.PrepareForwardSearch(pattern, line, column);
        }

        /// <summary>
        /// The (line, char) exclusive upper bound a backward regex search should stop before: the last line and
        /// <see cref="int.MaxValue"/> (clamped to each line's length by the searcher) when a backward wrap is
        /// pending, otherwise <paramref name="selectionStart"/>. Consumes the pending backward wrap.
        /// </summary>
        public (int Line, int Char) PlanRegexBackward(TextPosition selectionStart, int lineCount)
        {
            int line = backwardWrapPending ? Math.Max(0, lineCount - 1) : selectionStart.Line;
            int column = backwardWrapPending ? int.MaxValue : selectionStart.Column;
            backwardWrapPending = false;
            return (line, column);
        }

        /// <summary>
        /// Records a literal match (so a same-direction repeat resumes past/before it) and the
        /// <paramref name="newCaret"/> it left behind; clears both pending wraps.
        /// </summary>
        public void RecordLiteralMatch(long offset, int length, TextPosition newCaret)
        {
            literal.RecordMatch(offset, length);
            OnMatch(newCaret);
        }

        /// <summary>
        /// Records a regex match (line, start column, length) and the <paramref name="newCaret"/> it left
        /// behind; clears both pending wraps.
        /// </summary>
        public void RecordRegexMatch(int line, int charColumn, int length, TextPosition newCaret)
        {
            regex.RecordMatch(line, charColumn, length);
            OnMatch(newCaret);
        }

        /// <summary>
        /// Records that a literal search found nothing: arms the same-direction wrap for the next run and keeps
        /// <paramref name="caret"/> so a subsequent manual caret move is still detected.
        /// </summary>
        public void RecordLiteralNoMatch(FindDirection direction, TextPosition caret)
        {
            literal.RecordNoMatch();
            OnNoMatch(direction, caret);
        }

        /// <summary>Records that a regex search found nothing; see <see cref="RecordLiteralNoMatch"/>.</summary>
        public void RecordRegexNoMatch(FindDirection direction, TextPosition caret)
        {
            regex.RecordNoMatch();
            OnNoMatch(direction, caret);
        }

        /// <summary>Clears all find state (e.g. when the file is closed/switched or an option changes).</summary>
        public void Reset()
        {
            literal.Reset();
            regex.Reset();
            lastKey = null;
            lastCaret = null;
            forwardWrapPending = false;
            backwardWrapPending = false;
        }

        private void OnMatch(TextPosition newCaret)
        {
            lastCaret = newCaret;
            forwardWrapPending = false;
            backwardWrapPending = false;
        }

        private void OnNoMatch(FindDirection direction, TextPosition caret)
        {
            if (direction == FindDirection.Forward)
            {
                forwardWrapPending = true;
                backwardWrapPending = false;
            }
            else
            {
                backwardWrapPending = true;
                forwardWrapPending = false;
            }
            lastCaret = caret;
        }
    }
}
