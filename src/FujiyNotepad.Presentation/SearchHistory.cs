namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Pure MRU helpers for a search-term history list: newest-first ordering, case-insensitive de-duplication,
    /// and a fixed cap. No I/O, so it unit-tests directly. Mirrors <see cref="RecentFiles"/> but is kept separate
    /// because Find and Filter each have their own list and a larger cap than the recent-files list.
    /// </summary>
    public static class SearchHistory
    {
        /// <summary>Maximum number of remembered terms.</summary>
        public const int MaxCount = 20;

        /// <summary>
        /// Returns a new list with <paramref name="term"/> moved to the front, de-duplicated case-insensitively,
        /// and capped at <paramref name="max"/> entries. A null/blank term is ignored — the existing list is
        /// returned unchanged (as a copy).
        /// </summary>
        public static List<string> Add(IEnumerable<string> existing, string term, int max = MaxCount)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                return new List<string>(existing);
            }

            var result = new List<string> { term };
            foreach (string t in existing)
            {
                if (!string.Equals(t, term, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(t);
                }
            }

            if (max >= 0 && result.Count > max)
            {
                result.RemoveRange(max, result.Count - max);
            }

            return result;
        }
    }

    /// <summary>
    /// Drives shell-style Up/Down recall through a search-history list (newest first) for a single input box.
    /// <see cref="MoveUp"/> walks toward older entries; <see cref="MoveDown"/> walks back toward newer ones and
    /// finally the in-progress draft the user was typing before they started recalling. Editing the recalled
    /// text — so the box no longer matches what was last handed back — restarts the walk from that new draft.
    /// Pure and unit-tested: the host feeds in the box's current text and writes the returned text back, or
    /// ignores a <c>null</c> result meaning "nothing to recall, leave the box as-is".
    /// </summary>
    public sealed class SearchHistoryNavigator
    {
        private readonly IReadOnlyList<string> items; // newest first
        private int index = -1;                       // -1 = on the live draft line; 0..n-1 = into items
        private string draft = "";
        private string? lastRecalled;                 // what we last handed back, to detect a user edit

        public SearchHistoryNavigator(IReadOnlyList<string>? items)
            => this.items = items ?? Array.Empty<string>();

        /// <summary>
        /// Recalls the next older entry. <paramref name="current"/> is the box's text now. Returns the text to
        /// show, or <c>null</c> when there is nothing older (empty history, or already at the oldest entry).
        /// </summary>
        public string? MoveUp(string current)
        {
            if (items.Count == 0)
            {
                return null;
            }

            // A fresh walk, or the user edited the recalled text: capture the draft and start from the live line.
            if (index == -1 || !string.Equals(current, lastRecalled, StringComparison.Ordinal))
            {
                draft = current;
                index = -1;
            }

            if (index >= items.Count - 1)
            {
                return null; // already at the oldest entry
            }

            index++;
            lastRecalled = items[index];
            return lastRecalled;
        }

        /// <summary>
        /// Recalls the next newer entry, returning to the draft once past the newest. Returns the text to show,
        /// or <c>null</c> when already on the draft line.
        /// </summary>
        public string? MoveDown(string current)
        {
            if (index == -1)
            {
                return null; // already on the live draft line
            }

            // The user edited a recalled entry: keep their text as the new draft and stop walking.
            if (!string.Equals(current, lastRecalled, StringComparison.Ordinal))
            {
                draft = current;
                index = -1;
                return null;
            }

            index--;
            if (index == -1)
            {
                lastRecalled = null;
                return draft;
            }

            lastRecalled = items[index];
            return lastRecalled;
        }
    }
}
