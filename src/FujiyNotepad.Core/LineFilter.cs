namespace FujiyNotepad.Core
{
    /// <summary>
    /// Computes the set of lines to keep in the filter / grep view, optionally excluding matches. The scan is bounded
    /// (stops at a maximum number of kept lines) and cooperatively cancellable, so it stays responsive on huge
    /// files. The predicate (literal contains / regex / case option) is supplied by the caller, keeping this
    /// dependency-free and headlessly testable.
    /// </summary>
    public static class LineFilter
    {
        /// <summary>Default cap on kept lines, so a too-broad filter can't allocate without bound.</summary>
        public const int DefaultMaxMatches = 2_000_000;

        /// <summary>
        /// Returns the ascending indices of the lines in <paramref name="source"/> for which
        /// <paramref name="predicate"/> is true (false when <paramref name="exclude"/> is set), scanning
        /// <c>[0, LineCount)</c> and stopping once <paramref name="maxMatches"/> lines are kept
        /// (then <paramref name="capped"/> is true) or the
        /// <paramref name="token"/> is cancelled (which throws <see cref="OperationCanceledException"/>).
        /// </summary>
        public static List<int> Match(
            ILineSource source,
            Func<string, bool> predicate,
            out bool capped,
            int maxMatches = DefaultMaxMatches,
            IProgress<int>? progress = null,
            CancellationToken token = default,
            bool exclude = false)
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

                if (predicate(source.GetLineUncached(i)) != exclude)
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
        /// the literal byte <paramref name="pattern"/>, or do not contain it when <paramref name="exclude"/>
        /// is set. It scans the raw bytes with <paramref name="searcher"/>
        /// (no per-line decoding) and maps each match offset to its line via
        /// <see cref="LineIndexer.GetLineNumberFromOffset"/>, so its cost scales with the number of matches
        /// and kept lines rather than decoding every line — the fast equivalent of <see cref="Match"/> for a literal term.
        /// Exclusion collects gaps between matching lines as the scan advances, without allocating a match set
        /// or taking the complement of an already capped or cancelled scan.
        /// <para>
        /// Only offsets the index actually covers are mapped. The bound is taken once, up front: the source's
        /// cached length, further limited to the indexed frontier if the index is not yet complete. Matches
        /// beyond it end the scan, so the result is a snapshot of the file as indexed — the same semantics
        /// <see cref="Match"/> has by capturing <c>LineCount</c> once.
        /// </para>
        /// <para>
        /// The bound is deliberately the <em>cached</em> length rather than a fresh one. That makes it a
        /// conservative snapshot: indexing reads to the live end of file, so on a file that grew while it ran
        /// the index can cover a little past the cached length, and matches there are omitted rather than
        /// included. Omitting is the safe direction — the alternative is re-reading a region the bound exists
        /// to exclude — and it matches the rest of the viewer, which only re-reads the length while Follow Tail
        /// is on.
        /// </para>
        /// <para>
        /// This bound is what keeps the result honest, because two things move underneath the scan.
        /// <see cref="TextSearcher.Search"/> reads to the <em>live</em> end of file (its loop stops on a short
        /// read, not at a captured length), so a file that grows during the scan yields matches in a region no
        /// line index covers; and Follow Tail can clear <see cref="LineIndexer.IsCompleted"/> and resume
        /// indexing mid-scan. Unbounded, every such offset clamps onto the last indexed line — collapsing into
        /// one row that does not contain the term, and that can even sit past the provider's line count.
        /// Because the index is strictly append-only, an offset below the bound keeps mapping to the same line
        /// no matter what indexing does afterwards, so the bound alone makes both cases safe.
        /// </para>
        /// <para>
        /// Scanning stops once <paramref name="maxMatches"/> distinct kept lines are collected (then the returned
        /// <c>Capped</c> is true). Cancellation is cooperative, mirroring <see cref="TextSearcher.Search"/>: a
        /// cancelled <paramref name="token"/> ends the scan and returns the lines found so far rather than
        /// throwing, so the caller decides whether to keep or discard them.
        /// Exclusion throws <see cref="EndOfStreamException"/> if the source ends before the captured
        /// snapshot has been read, rather than treating unscanned rows as nonmatching.
        /// </para>
        /// </summary>
        public static async Task<(List<int> Lines, bool Capped)> MatchLinesByPatternAsync(
            TextSearcher searcher,
            LineIndexer indexer,
            byte[] pattern,
            SearchOptions options,
            int maxMatches = DefaultMaxMatches,
            IProgress<int>? progress = null,
            CancellationToken token = default,
            bool exclude = false)
        {
            var lines = new List<int>();
            try
            {
                token.ThrowIfCancellationRequested();
                int lastLine = -1;

                // Capture the covered region once, before scanning. Snapshot IsCompleted into a local first: if it
                // flips to false right afterwards, indexing has only appended, so this bound stays conservative
                // rather than stale.
                bool completeAtStart = indexer.IsCompleted;
                long snapshotLength = searcher.SourceLength;
                long limit = snapshotLength;
                if (!completeAtStart)
                {
                    limit = Math.Min(limit, indexer.IndexedFrontier);
                }

                // The final byte belongs to the last covered line, even with a trailing newline. Deriving the
                // count from this same bound avoids phantom rows and rows indexed past the cached file length.
                int totalLines = exclude && limit > 0 ? indexer.GetLineNumberFromOffset(limit - 1, token) + 1 : 0;
                int nextLine = 0;

                bool CollectNonMatchingLines(int endExclusive)
                {
                    while (nextLine < endExclusive)
                    {
                        token.ThrowIfCancellationRequested();
                        lines.Add(nextLine++);
                        if (lines.Count >= maxMatches)
                        {
                            return true;
                        }
                    }
                    return false;
                }

                // Bound the scan itself, not just the mapping. Exclusion also requires reaching that snapshot:
                // an early EOF must not classify the unread tail as nonmatching.
                await foreach (long offset in searcher.Search(
                    0, pattern, options, progress, token, limit, requireCompleteRead: exclude, snapshotLength: snapshotLength))
                {
                    token.ThrowIfCancellationRequested();
                    if (offset >= limit)
                    {
                        // Past what the index covers; mapping from here on would clamp onto the last indexed line.
                        break;
                    }

                    int line = indexer.GetLineNumberFromOffset(offset, token);

                    // Matches arrive in ascending offset order, so their line numbers are non-decreasing: a match on
                    // the same line as the previous one is a duplicate to skip, yielding an ascending, distinct list.
                    if (line != lastLine)
                    {
                        lastLine = line;
                        if (exclude)
                        {
                            if (CollectNonMatchingLines(line))
                            {
                                return (lines, true);
                            }
                            nextLine = line + 1;
                        }
                        else
                        {
                            lines.Add(line);
                            if (lines.Count >= maxMatches)
                            {
                                return (lines, true);
                            }
                        }
                    }
                }

                token.ThrowIfCancellationRequested();
                bool capped = exclude && CollectNonMatchingLines(totalLines);
                return (lines, capped);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return (lines, false);
            }
        }
    }
}
