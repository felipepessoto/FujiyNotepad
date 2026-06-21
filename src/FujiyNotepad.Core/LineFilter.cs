namespace FujiyNotepad.Core
{
    /// <summary>
    /// Computes the set of lines that match a predicate, for the filter / grep view. The scan is bounded
    /// (stops at a maximum number of matches) and cooperatively cancellable, so it stays responsive on huge
    /// files. The predicate (literal contains / regex / case option) is supplied by the caller, keeping this
    /// dependency-free and headlessly testable.
    /// </summary>
    public static class LineFilter
    {
        /// <summary>Default cap on collected matches, so a too-broad filter can't allocate without bound.</summary>
        public const int DefaultMaxMatches = 2_000_000;

        /// <summary>
        /// Returns the ascending indices of the lines in <paramref name="source"/> for which
        /// <paramref name="predicate"/> is true, scanning <c>[0, LineCount)</c> and stopping once
        /// <paramref name="maxMatches"/> are found (then <paramref name="capped"/> is true) or the
        /// <paramref name="token"/> is cancelled (which throws <see cref="OperationCanceledException"/>).
        /// </summary>
        public static List<int> Match(
            ILineSource source,
            Func<string, bool> predicate,
            out bool capped,
            int maxMatches = DefaultMaxMatches,
            IProgress<int>? progress = null,
            CancellationToken token = default)
        {
            capped = false;
            var result = new List<int>();
            int total = source.LineCount;
            int lastPercent = -1;

            for (int i = 0; i < total; i++)
            {
                // Check cancellation and report progress periodically rather than every line, so the hot loop
                // stays tight and the UI receives at most ~100 updates.
                if ((i & 0x3FFF) == 0)
                {
                    token.ThrowIfCancellationRequested();

                    if (progress != null)
                    {
                        int percent = (int)((long)i * 100 / total);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress.Report(percent);
                        }
                    }
                }

                if (predicate(source.GetLineUncached(i)))
                {
                    result.Add(i);
                    if (result.Count >= maxMatches)
                    {
                        capped = true;
                        break;
                    }
                }
            }

            progress?.Report(100);
            return result;
        }

        /// <summary>
        /// Returns the ascending, de-duplicated 0-based indices of the lines that contain at least one match of
        /// the literal byte <paramref name="pattern"/>. It scans the raw bytes with <paramref name="searcher"/>
        /// (no per-line decoding) and maps each match offset to its line via
        /// <see cref="LineIndexer.GetLineNumberFromOffset"/>, so its cost scales with the number of matches
        /// rather than the total line count — the fast equivalent of <see cref="Match"/> for a literal term.
        /// <para>
        /// The caller must ensure <paramref name="indexer"/> is fully built (<see cref="LineIndexer.IsCompleted"/>):
        /// past the indexed frontier <see cref="LineIndexer.GetLineNumberFromOffset"/> clamps to the last indexed
        /// line, which would map later matches onto the wrong line. Scanning stops once <paramref name="maxMatches"/>
        /// distinct lines are collected (then the returned <c>Capped</c> is true). Cancellation is cooperative,
        /// mirroring <see cref="TextSearcher.Search"/>: a cancelled <paramref name="token"/> ends the scan and
        /// returns the lines found so far rather than throwing, so the caller decides whether to keep or discard them.
        /// </para>
        /// </summary>
        public static async Task<(List<int> Lines, bool Capped)> MatchLinesByPatternAsync(
            TextSearcher searcher,
            LineIndexer indexer,
            byte[] pattern,
            SearchOptions options,
            int maxMatches = DefaultMaxMatches,
            IProgress<int>? progress = null,
            CancellationToken token = default)
        {
            var lines = new List<int>();
            int lastLine = -1;

            await foreach (long offset in searcher.Search(0, pattern, options, progress, token))
            {
                int line = indexer.GetLineNumberFromOffset(offset);

                // Matches arrive in ascending offset order, so their line numbers are non-decreasing: a match on
                // the same line as the previous one is a duplicate to skip, yielding an ascending, distinct list.
                if (line != lastLine)
                {
                    lines.Add(line);
                    lastLine = line;
                    if (lines.Count >= maxMatches)
                    {
                        return (lines, true);
                    }
                }
            }

            return (lines, false);
        }
    }
}
