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
    }
}
