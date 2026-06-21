namespace FujiyNotepad.Core
{
    /// <summary>
    /// Maps display lines to byte offsets while keeping index memory <b>sub-linear</b> in line count, so even
    /// 100M+ line files spend well under a megabyte on the index (a flat one-offset-per-line list would be
    /// ~8 bytes × lines, i.e. ~800 MB–1 GB at 100M lines). Instead of every line start, only a <b>checkpoint</b>
    /// offset every <see cref="CheckpointInterval"/> lines is retained; an arbitrary line's start is
    /// reconstructed by reading from the nearest checkpoint and scanning forward the remaining newlines. A
    /// small LRU cache of recently expanded checkpoint blocks keeps the cost of the (sequential, ~50-line)
    /// viewport negligible.
    ///
    /// <para>The background indexer is the sole writer of the index state (<see cref="checkpoints"/>,
    /// <see cref="lineStartCount"/>, <see cref="lastLineStart"/>); lookups run on the UI thread. All index
    /// state is guarded by <see cref="indexLock"/>; the per-block expansion reads the source outside the lock
    /// (the source allows concurrent positional reads), so a lookup never blocks indexing on I/O.</para>
    /// </summary>
    public class LineIndexer
    {
        // One checkpoint per this many lines. Memory is ~8 bytes × lines / K; K = 1024 gives < 1 MB of
        // checkpoints at 100M lines, while a block scan reads only ~K newlines (one chunk for typical lines).
        private const int CheckpointInterval = 1024;

        // Recently expanded blocks (block index -> that block's line-start offsets). The viewport is ~50 lines
        // and scrolling is incremental, so a handful of blocks serves nearly all lookups from memory.
        private const int MaxCachedBlocks = 16;

        // checkpoints[j] is the byte offset of line (j * CheckpointInterval) — i.e. the start of the first line
        // in block j. checkpoints[0] is the start of line 0 (offset 0). One entry is added per K line starts.
        private readonly List<long> checkpoints = new List<long>();

        // Number of known line starts: lineStart(0)..lineStart(lineStartCount-1). lineStart(0) = 0 once seeded.
        private int lineStartCount;

        // Offset of the most recently discovered line start (= lineStart(lineStartCount-1)); used to resume a
        // paused index and as the indexed frontier for CanResolveOffset, both without expanding a block.
        private long lastLineStart;

        // UI-thread block cache (block index -> expanded line-start offsets) with LRU eviction. Guarded by
        // indexLock so it stays consistent if a lookup ever runs off the UI thread; the I/O to fill it happens
        // outside the lock.
        private readonly Dictionary<int, long[]> blockCache = new Dictionary<int, long[]>();
        private readonly LinkedList<int> blockLru = new LinkedList<int>();

        private readonly object indexLock = new object();
        public static readonly byte[] LineBreak = { (byte)'\n' };

        private readonly TextSearcher searcher;
        private readonly TextEncoding encoding;
        private readonly SearchOptions newlineOptions;

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
            // Match the indexing pass's newline search: code-unit aligned so a byte pattern straddling two
            // characters in a multi-byte encoding is not mistaken for a newline.
            newlineOptions = new SearchOptions { UnitAlignment = this.encoding.CodeUnitSize };
        }

        public async Task StartTaskToIndexLines(CancellationToken cancelToken, IProgress<int> progress)
        {
            long startOffset;

            lock (indexLock)
            {
                if (lineStartCount == 0)
                {
                    // Seed: line 0 starts at offset 0 and is the first checkpoint.
                    checkpoints.Add(0);
                    lineStartCount = 1;
                    lastLineStart = 0;
                    startOffset = 0;
                }
                else
                {
                    startOffset = lastLineStart; // resume past the last known line start
                }
            }

            // Search for the encoding's newline sequence (code-unit aligned). Cancellation is observed here via
            // ThrowIfCancellationRequested, which signals a stop by throwing OperationCanceledException (caught
            // by the caller to re-enable resuming and to avoid marking a partial index complete). The next line
            // starts past the whole newline sequence.
            await foreach (long result in searcher.Search(startOffset, encoding.NewLineBytes, newlineOptions, progress))
            {
                cancelToken.ThrowIfCancellationRequested();
                long lineStart = result + encoding.NewLineBytes.Length;
                lock (indexLock)
                {
                    // Keep only a checkpoint at every K-th line start; the rest are reconstructed on demand.
                    if (lineStartCount % CheckpointInterval == 0)
                    {
                        checkpoints.Add(lineStart);
                    }
                    lineStartCount++;
                    lastLineStart = lineStart;
                }
            }

            IsCompleted = true;
        }

        /// <summary>
        /// Returns the byte offset of the <paramref name="lineNumber"/>-th index entry, preserving the original
        /// flat-index contract: entry 0 is a dummy (0), and entry n (n ≥ 1) is the start offset of display line
        /// (n − 1). Out-of-range numbers throw, exactly as the flat list's indexer did.
        /// </summary>
        public long GetOffsetFromLineNumber(int lineNumber)
        {
            int block;
            long checkpointOffset;
            int knownInBlock;

            lock (indexLock)
            {
                int entryCount = lineStartCount == 0 ? 0 : lineStartCount + 1;
                if (lineNumber < 0 || lineNumber >= entryCount)
                {
                    throw new InvalidOperationException();
                }
                if (lineNumber == 0)
                {
                    return 0; // the dummy [0] entry
                }

                int line = lineNumber - 1; // 0-based line-start index
                block = line / CheckpointInterval;
                checkpointOffset = checkpoints[block];
                knownInBlock = (int)Math.Min(CheckpointInterval, (long)lineStartCount - (long)block * CheckpointInterval);
            }

            long[] starts = GetBlockStarts(block, checkpointOffset, knownInBlock);
            return starts[(lineNumber - 1) - block * CheckpointInterval];
        }

        public int GetNumberOfLinesIndexed()
        {
            lock (indexLock)
            {
                // Mirror the original [dummy + one-per-line] entry count (0 before seeding).
                return lineStartCount == 0 ? 0 : lineStartCount + 1;
            }
        }

        /// <summary>
        /// Returns the 0-based display line that contains <paramref name="offset"/>: the checkpoints are
        /// binary-searched for the block holding the offset, then that block is expanded and scanned for the
        /// exact line. If indexing has not yet reached the offset, the last indexed line is returned (a clamping
        /// fallback rather than an exact, not-yet-known answer).
        /// </summary>
        public int GetLineNumberFromOffset(long offset)
        {
            int block;
            long checkpointOffset;
            int knownInBlock;
            int firstLineOfBlock;

            lock (indexLock)
            {
                if (lineStartCount == 0)
                {
                    return 0;
                }

                // Largest j with checkpoints[j] <= offset → the block whose first line is at or before offset.
                int lo = 0;
                int hi = checkpoints.Count - 1;
                int found = 0;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (checkpoints[mid] <= offset)
                    {
                        found = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                block = found;
                checkpointOffset = checkpoints[block];
                firstLineOfBlock = block * CheckpointInterval;
                knownInBlock = (int)Math.Min(CheckpointInterval, (long)lineStartCount - firstLineOfBlock);
            }

            long[] starts = GetBlockStarts(block, checkpointOffset, knownInBlock);

            // Largest local i with starts[i] <= offset (starts[0] <= offset always, by the checkpoint search).
            int loi = 0;
            int hii = starts.Length - 1;
            int resi = 0;
            while (loi <= hii)
            {
                int mid = (loi + hii) >> 1;
                if (starts[mid] <= offset)
                {
                    resi = mid;
                    loi = mid + 1;
                }
                else
                {
                    hii = mid - 1;
                }
            }

            return firstLineOfBlock + resi;
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
                return lineStartCount >= 1 && offset < lastLineStart;
            }
        }

        // Returns block b's line-start offsets (length = knownInBlock), from the cache when present and current,
        // otherwise by expanding from the checkpoint (reading the source outside the lock) and caching the result.
        private long[] GetBlockStarts(int block, long checkpointOffset, int knownInBlock)
        {
            lock (indexLock)
            {
                // A cached entry is reusable when it covers at least as many lines as are now known in the block
                // (a previously partial frontier block is re-expanded once indexing advances past it).
                if (blockCache.TryGetValue(block, out long[]? cached) && cached.Length >= knownInBlock)
                {
                    TouchBlock(block);
                    return cached;
                }
            }

            long[] starts = ExpandBlock(checkpointOffset, knownInBlock);

            lock (indexLock)
            {
                blockCache[block] = starts;
                TouchBlock(block);
                while (blockLru.Count > MaxCachedBlocks)
                {
                    int evict = blockLru.Last!.Value;
                    blockLru.RemoveLast();
                    blockCache.Remove(evict);
                }
            }

            return starts;
        }

        // Reads up to (knownInBlock - 1) newlines forward from the checkpoint and turns them into line-start
        // offsets [checkpoint, newline0+len, newline1+len, ...]. I/O happens here, outside indexLock.
        private long[] ExpandBlock(long checkpointOffset, int knownInBlock)
        {
            if (knownInBlock <= 1)
            {
                return new[] { checkpointOffset };
            }

            // Expand straight into the array that will be cached: write the (knownInBlock - 1) newline offsets
            // into starts[1..] with no intermediate list (issue #136), then shift each past the newline to get
            // the next line's start. starts[0] is the checkpoint (the block's first line start).
            long[] starts = new long[knownInBlock];
            starts[0] = checkpointOffset;
            int found = searcher.FindForward(checkpointOffset, encoding.NewLineBytes, newlineOptions, starts.AsSpan(1));
            int nlLen = encoding.NewLineBytes.Length;
            for (int i = 1; i <= found; i++)
            {
                starts[i] += nlLen;
            }

            if (found == knownInBlock - 1)
            {
                return starts;
            }

            // Rare: the source was truncated since indexing, so fewer newlines exist than expected. Trim to the
            // line starts actually found so callers never read a stale (zero) tail slot as a line start.
            long[] trimmed = new long[1 + found];
            Array.Copy(starts, trimmed, 1 + found);
            return trimmed;
        }

        // Moves a block to the front of the LRU list (most-recently-used). Called under indexLock.
        private void TouchBlock(int block)
        {
            blockLru.Remove(block);
            blockLru.AddFirst(block);
        }

        // Test-only: installs a partial, not-completed index — line 0 at offset 0 plus the given line starts —
        // so the index-readiness guard (CanResolveOffset) and the line counts can be exercised deterministically
        // without racing a background indexing task. It does not back GetOffsetFromLineNumber/
        // GetLineNumberFromOffset reconstruction, which scans the real source from a checkpoint.
        internal void SetPartialIndexForTest(params long[] lineStarts)
        {
            lock (indexLock)
            {
                checkpoints.Clear();
                blockCache.Clear();
                blockLru.Clear();

                checkpoints.Add(0);
                lineStartCount = 1;
                lastLineStart = 0;

                foreach (long start in lineStarts)
                {
                    if (lineStartCount % CheckpointInterval == 0)
                    {
                        checkpoints.Add(start);
                    }
                    lineStartCount++;
                    lastLineStart = start;
                }

                isCompleted = false;
            }
        }
    }
}
