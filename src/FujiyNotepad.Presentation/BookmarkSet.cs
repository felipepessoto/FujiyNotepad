namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// The set of bookmarked line indices for the current document, kept sorted so jumping to the next/previous
    /// bookmark (with wrap-around) is straightforward. Pure and headlessly testable: the engine owns one, the
    /// gutter paints a marker for each contained line, and it is cleared when a new file is opened.
    /// </summary>
    public sealed class BookmarkSet
    {
        private readonly SortedSet<int> lines = new();

        /// <summary>Number of bookmarked lines.</summary>
        public int Count => lines.Count;

        /// <summary>The bookmarked line indices in ascending order.</summary>
        public IReadOnlyCollection<int> Lines => lines;

        /// <summary>Toggles the bookmark on <paramref name="line"/>; returns true if it is now bookmarked.</summary>
        public bool Toggle(int line)
        {
            if (lines.Remove(line))
            {
                return false;
            }
            lines.Add(line);
            return true;
        }

        /// <summary>True when <paramref name="line"/> is bookmarked.</summary>
        public bool Contains(int line) => lines.Contains(line);

        /// <summary>Removes every bookmark.</summary>
        public void Clear() => lines.Clear();

        /// <summary>
        /// The nearest bookmarked line strictly greater than <paramref name="line"/>, wrapping to the first
        /// bookmark when none is larger. Null when there are no bookmarks.
        /// </summary>
        public int? NextAfter(int line)
        {
            if (lines.Count == 0)
            {
                return null;
            }

            foreach (int l in lines)
            {
                if (l > line)
                {
                    return l;
                }
            }
            return lines.Min; // past the last bookmark -> wrap to the first
        }

        /// <summary>
        /// The nearest bookmarked line strictly less than <paramref name="line"/>, wrapping to the last
        /// bookmark when none is smaller. Null when there are no bookmarks.
        /// </summary>
        public int? PreviousBefore(int line)
        {
            if (lines.Count == 0)
            {
                return null;
            }

            int? best = null;
            foreach (int l in lines)
            {
                if (l < line)
                {
                    best = l;
                }
                else
                {
                    break;
                }
            }
            return best ?? lines.Max; // before the first bookmark -> wrap to the last
        }
    }
}
