namespace FujiyNotepad.Core
{
    public class LineIndexer
    {
        // Append-only list of line-start offsets. Seeded with two zeros (a dummy at [0] plus the start
        // of the first line at [1]); one entry is appended per '\n' found. The background indexer is the
        // sole writer; the UI thread reads it on every rendered frame, so all access is guarded by a lock.
        private readonly List<long> lineNumberIndex = new List<long>();
        private readonly object indexLock = new object();
        public static readonly byte[] LineBreak = { (byte)'\n' };

        private readonly TextSearcher searcher;
        private readonly TextEncoding encoding;

        private volatile bool isCompleted;
        public bool IsCompleted
        {
            get => isCompleted;
            set => isCompleted = value;
        }

        public LineIndexer(TextSearcher searcher, TextEncoding? encoding = null)
        {
            this.searcher = searcher;
            this.encoding = encoding ?? TextEncoding.Utf8;
        }

        public async Task StartTaskToIndexLines(CancellationToken cancelToken, IProgress<int> progress)
        {
            long startOffset;

            lock (indexLock)
            {
                if (lineNumberIndex.Count == 0)
                {
                    lineNumberIndex.Add(0);
                    lineNumberIndex.Add(0);
                    startOffset = 0;
                }
                else
                {
                    startOffset = lineNumberIndex[lineNumberIndex.Count - 1];
                }
            }

            // Search for the encoding's newline sequence, restricted to code-unit boundaries so a byte
            // pattern that straddles two characters in a multi-byte encoding isn't mistaken for a newline.
            // Pass None-equivalent options (only the alignment) so it keeps yielding line breaks; cancellation
            // is observed here via ThrowIfCancellationRequested, which signals a stop by throwing
            // OperationCanceledException (caught by the caller to re-enable resuming and to avoid marking a
            // partial index complete). The next line starts past the whole newline sequence.
            var options = new SearchOptions { UnitAlignment = encoding.CodeUnitSize };
            await foreach (long result in searcher.Search(startOffset, encoding.NewLineBytes, options, progress))
            {
                cancelToken.ThrowIfCancellationRequested();
                lock (indexLock)
                {
                    lineNumberIndex.Add(result + encoding.NewLineBytes.Length);
                }
            }

            IsCompleted = true;
        }

        public long GetOffsetFromLineNumber(int lineNumber)
        {
            lock (indexLock)
            {
                if (lineNumberIndex.Count > lineNumber)
                {
                    return lineNumberIndex[lineNumber];
                }
            }

            throw new InvalidOperationException();
        }

        public int GetNumberOfLinesIndexed()
        {
            lock (indexLock)
            {
                return lineNumberIndex.Count;
            }
        }

        /// <summary>
        /// Returns the 0-based display line that contains <paramref name="offset"/>, found by binary
        /// search over the indexed line starts. If indexing has not yet reached the offset, the last
        /// indexed line is returned (a clamping fallback rather than an exact, not-yet-known answer).
        /// </summary>
        public int GetLineNumberFromOffset(long offset)
        {
            lock (indexLock)
            {
                // Entries [1 .. Count-1] are ascending line starts; [0] is the dummy seed.
                int lo = 1;
                int hi = lineNumberIndex.Count - 1;
                int result = 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (lineNumberIndex[mid] <= offset)
                    {
                        result = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                return result - 1; // Convert 1-based index entry to 0-based display line.
            }
        }

        /// <summary>
        /// True if a match at <paramref name="offset"/> can be resolved to its exact line without reading
        /// past the indexed region: either indexing has completed, or the offset lies before the last
        /// indexed line start (so the line containing it has a known end). When this is false the offset is
        /// beyond the indexed frontier; the caller should wait for indexing to catch up rather than resolve
        /// it, which would clamp to the last indexed line and read up to the rest of the file.
        /// </summary>
        public bool CanResolveOffset(long offset)
        {
            if (isCompleted)
            {
                return true;
            }

            lock (indexLock)
            {
                return lineNumberIndex.Count >= 2 && offset < lineNumberIndex[lineNumberIndex.Count - 1];
            }
        }

        // Test-only: installs a partial, not-completed index (the [0, 0] seed plus the given line-start
        // offsets) so the index-readiness guard can be exercised deterministically without racing a
        // background indexing task.
        internal void SetPartialIndexForTest(params long[] lineStarts)
        {
            lock (indexLock)
            {
                lineNumberIndex.Clear();
                lineNumberIndex.Add(0);
                lineNumberIndex.Add(0);
                lineNumberIndex.AddRange(lineStarts);
                isCompleted = false;
            }
        }
    }
}
