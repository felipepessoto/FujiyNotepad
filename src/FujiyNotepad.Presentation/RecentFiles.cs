namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Pure helpers for the recently-opened-files (MRU) list: newest-first ordering, case-insensitive
    /// de-duplication, a fixed cap, and pruning of entries that no longer exist. No I/O — existence is
    /// supplied as a delegate — so it unit-tests directly.
    /// </summary>
    public static class RecentFiles
    {
        /// <summary>Maximum number of entries kept in the list.</summary>
        public const int MaxCount = 10;

        /// <summary>
        /// Returns a new list with <paramref name="path"/> moved to the front, de-duplicated
        /// case-insensitively, and capped at <paramref name="max"/> entries. A null/blank path is ignored
        /// (the existing list is returned unchanged).
        /// </summary>
        public static List<string> Add(IEnumerable<string> existing, string path, int max = MaxCount)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return new List<string>(existing);
            }

            var result = new List<string> { path };
            foreach (string p in existing)
            {
                if (!string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(p);
                }
            }

            if (max >= 0 && result.Count > max)
            {
                result.RemoveRange(max, result.Count - max);
            }

            return result;
        }

        /// <summary>Returns a new list keeping only entries for which <paramref name="exists"/> is true.</summary>
        public static List<string> Prune(IEnumerable<string> existing, Func<string, bool> exists)
        {
            var result = new List<string>();
            foreach (string p in existing)
            {
                if (exists(p))
                {
                    result.Add(p);
                }
            }
            return result;
        }
    }
}
