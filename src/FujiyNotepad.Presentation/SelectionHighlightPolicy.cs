namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Decides whether the current selection should trigger "highlight all occurrences of the selected text"
    /// (issue #130), and extracts the term. Pure and device-free so the rule is unit-tested without a view.
    ///
    /// The highlight is a low-friction reading aid, so it only fires for a "wordy" single-line selection: it
    /// must be on one line, non-empty, not just whitespace, and within a length bound (so selecting a whole
    /// paragraph-like long line doesn't flood the view or cost too much per visible line).
    /// </summary>
    public static class SelectionHighlightPolicy
    {
        public const int MinLength = 1;
        public const int MaxLength = 200;

        /// <summary>
        /// Returns the term to highlight for a selection spanning <paramref name="selectedText"/>, or null when
        /// the selection should not trigger occurrence highlighting. <paramref name="isSingleLine"/> is the
        /// view's own determination (the selection's start and end share a source line).
        /// </summary>
        public static string? TermFor(string? selectedText, bool isSingleLine)
        {
            if (!isSingleLine || selectedText is null)
            {
                return null;
            }

            int length = selectedText.Length;
            if (length < MinLength || length > MaxLength)
            {
                return null;
            }

            if (IsAllWhitespace(selectedText))
            {
                return null;
            }

            return selectedText;
        }

        private static bool IsAllWhitespace(string s)
        {
            foreach (char c in s)
            {
                if (!char.IsWhiteSpace(c))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
