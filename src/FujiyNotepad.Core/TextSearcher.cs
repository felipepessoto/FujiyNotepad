using System.Runtime.CompilerServices;

namespace FujiyNotepad.Core
{
    /// <summary>
    /// Forward/backward byte-pattern search over an <see cref="IByteSource"/>. Reads the source in
    /// large chunks and scans them with the vectorized span search, which is dramatically faster than
    /// a byte-at-a-time scan on large files.
    /// </summary>
    public sealed class TextSearcher
    {
        private const int DefaultChunkSize = 1 << 20; // 1 MiB

        private readonly IByteSource source;
        private readonly int chunkSize;

        public TextSearcher(IByteSource source) : this(source, DefaultChunkSize)
        {
        }

        internal TextSearcher(IByteSource source, int chunkSize)
        {
            this.source = source;
            this.chunkSize = chunkSize;
        }

        /// <summary>
        /// Yields the absolute offset of every non-overlapping occurrence of <paramref name="pattern"/> at
        /// or after <paramref name="startOffset"/>: after each match, scanning resumes past its end (like a
        /// text editor's "find all"), so "xxx" in "xxxxxx" matches at 0 and 3, not 0/1/2/3.
        /// Cancellation is cooperative: a cancelled <paramref name="token"/> stops enumeration between
        /// chunks <em>without throwing</em>, so callers can treat a cancelled search as an empty result.
        /// </summary>
        public IAsyncEnumerable<long> Search(long startOffset, byte[] pattern, IProgress<int>? progress = null, CancellationToken token = default)
            => Search(startOffset, pattern, default, progress, token);

        /// <summary>
        /// As <see cref="Search(long, byte[], IProgress{int}?, CancellationToken)"/>, additionally honouring
        /// <paramref name="options"/>: ASCII case-insensitive matching and/or whole-word filtering.
        /// </summary>
        public async IAsyncEnumerable<long> Search(long startOffset, byte[] pattern, SearchOptions options, IProgress<int>? progress = null, [EnumeratorCancellation] CancellationToken token = default)
        {
            if (pattern.Length == 0)
            {
                yield break;
            }

            byte[]? foldedPattern = options.IgnoreCase ? Fold(pattern) : null;

            long length = source.Length;
            if (startOffset < 0)
            {
                startOffset = 0;
            }
            if (startOffset >= length)
            {
                progress?.Report(100);
                yield break;
            }

            int overlap = pattern.Length - 1;
            byte[] buffer = new byte[chunkSize + overlap];

            long readPos = startOffset;     // next absolute read position
            int carry = 0;                  // bytes retained at the front of the buffer
            long bufferBase = startOffset;  // absolute offset of buffer[0]
            long nextAllowedStart = startOffset; // non-overlapping guard: never yield a match before this
            long total = length - startOffset;
            int lastPercent = -1;
            progress?.Report(0);

            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    yield break;
                }

                // The token is intentionally NOT forwarded to the read: cancellation stays cooperative
                // (checked above) so the read never throws OperationCanceledException on the caller.
                int read = await ReadFullAsync(readPos, buffer.AsMemory(carry, chunkSize));
                int available = carry + read;
                if (available == 0)
                {
                    break;
                }

                // Yield each match as it is found. The span is a short-lived temporary (never a local
                // held across the yield), so callers like Find can stop without scanning the whole chunk.
                int from = 0;
                while (from < available)
                {
                    int idx = IndexOf(buffer.AsSpan(from, available - from), pattern, foldedPattern);
                    if (idx < 0)
                    {
                        break;
                    }
                    int matchAt = from + idx;
                    long matchOffset = bufferBase + matchAt;

                    // A real match resumes scanning past its end so matches never overlap. Skip a candidate
                    // that overlaps one already yielded (this can recur at a chunk boundary, where the carry
                    // re-presents the tail of the previous match) or a rejected whole-word candidate, trying
                    // the next byte instead.
                    if (matchOffset >= nextAllowedStart
                        && (!options.WholeWord || IsWholeWordMatch(buffer, available, bufferBase, matchAt, pattern.Length)))
                    {
                        yield return matchOffset;
                        nextAllowedStart = matchOffset + pattern.Length;
                        from = matchAt + pattern.Length;
                    }
                    else
                    {
                        from = matchAt + 1;
                    }
                }

                readPos += read;

                bool eof = read < chunkSize;
                int newCarry = eof ? 0 : Math.Min(overlap, available);
                if (newCarry > 0)
                {
                    // Carry the last (pattern.Length - 1) bytes so a match straddling the chunk
                    // boundary is found at the start of the next buffer.
                    buffer.AsSpan(available - newCarry, newCarry).CopyTo(buffer);
                }
                bufferBase = readPos - newCarry;
                carry = newCarry;

                if (progress != null)
                {
                    int percent = (int)((readPos - startOffset) * 100 / total);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(percent);
                    }
                }

                if (eof)
                {
                    break;
                }
            }

            progress?.Report(100);
        }

        /// <summary>
        /// Returns the absolute offset of the last (highest-offset) occurrence of <paramref name="pattern"/>
        /// whose start is strictly before <paramref name="beforeOffset"/>, honouring <paramref name="options"/>
        /// (ASCII case-insensitive and/or whole-word), or <c>null</c> when there is none. This is the backward
        /// counterpart of <see cref="Search(long, byte[], SearchOptions, IProgress{int}?, CancellationToken)"/>
        /// used for "Find Previous": it walks the source in chunks from high to low and stops at the first
        /// (highest) match. For a self-overlapping pattern the result is the nearest non-overlapping match
        /// anchored at <paramref name="beforeOffset"/>, which can differ from the forward scan's canonical set
        /// only in pathological cases (e.g. "xx" inside a long run of x's). Cancellation is cooperative: a
        /// cancelled <paramref name="token"/> stops the scan between chunks without throwing and returns <c>null</c>.
        /// </summary>
        public long? FindLastBefore(long beforeOffset, byte[] pattern, SearchOptions options = default, CancellationToken token = default)
        {
            if (pattern.Length == 0)
            {
                return null;
            }

            long length = source.Length;
            int patLen = pattern.Length;
            int overlap = patLen - 1;

            // Valid match starts lie in [0, startCap): strictly before the caret and with room for the
            // whole pattern before end-of-file.
            long startCap = Math.Min(beforeOffset, length - patLen + 1);
            if (startCap <= 0)
            {
                return null;
            }

            byte[]? foldedPattern = options.IgnoreCase ? Fold(pattern) : null;
            byte[] buffer = new byte[chunkSize + overlap];

            long pos = startCap; // exclusive upper bound for match starts not yet covered by a higher block
            while (pos > 0)
            {
                if (token.IsCancellationRequested)
                {
                    return null;
                }

                int toScan = (int)Math.Min(chunkSize, pos);
                long blockStart = pos - toScan;

                // Read the block plus the overlap needed to verify a match that starts near its high edge.
                long windowEnd = Math.Min(pos + overlap, length);
                int windowLen = (int)(windowEnd - blockStart);
                int read = source.ReadFull(blockStart, buffer.AsSpan(0, windowLen));

                // Forward-scan the window, keeping the highest valid start below pos; starts at/after pos
                // live in the overlap region and belong to an already-scanned higher block.
                long best = -1;
                int from = 0;
                while (from < read)
                {
                    int idx = IndexOf(buffer.AsSpan(from, read - from), pattern, foldedPattern);
                    if (idx < 0)
                    {
                        break;
                    }
                    int matchAt = from + idx;
                    long matchOffset = blockStart + matchAt;
                    if (matchOffset >= pos)
                    {
                        break;
                    }
                    if (!options.WholeWord || IsWholeWordMatch(buffer, read, blockStart, matchAt, patLen))
                    {
                        best = matchOffset;
                    }
                    from = matchAt + 1;
                }

                if (best >= 0)
                {
                    return best;
                }

                pos = blockStart;
            }

            return null;
        }

        /// <summary>
        /// Yields the absolute offset of every occurrence of <paramref name="value"/> strictly before
        /// <paramref name="startOffset"/>, in descending order. For '\n' it additionally yields -1 at
        /// the end (the implicit start-of-file line boundary the viewport relies on).
        /// </summary>
        public IEnumerable<long> SearchBackward(long startOffset, byte value)
        {
            if (startOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startOffset), $"{nameof(startOffset)} cannot be negative");
            }

            byte[] buffer = new byte[chunkSize];
            long pos = Math.Min(startOffset, source.Length); // scan the range [0, pos)

            while (pos > 0)
            {
                int toRead = (int)Math.Min(chunkSize, pos);
                long blockStart = pos - toRead;
                int read = source.ReadFull(blockStart, buffer.AsSpan(0, toRead));

                int end = read;
                while (end > 0)
                {
                    int idx = buffer.AsSpan(0, end).LastIndexOf(value);
                    if (idx < 0)
                    {
                        break;
                    }
                    yield return blockStart + idx;
                    end = idx;
                }

                pos = blockStart;
            }

            if (value == (byte)'\n')
            {
                yield return -1;
            }
        }

        private static byte AsciiFold(byte b) => (byte)(b >= (byte)'A' && b <= (byte)'Z' ? b + 32 : b);

        private static byte[] Fold(ReadOnlySpan<byte> pattern)
        {
            byte[] folded = new byte[pattern.Length];
            for (int i = 0; i < pattern.Length; i++)
            {
                folded[i] = AsciiFold(pattern[i]);
            }
            return folded;
        }

        // Case-sensitive when foldedPattern is null (vectorized span IndexOf). Otherwise an ASCII
        // case-insensitive scan that still uses a vectorized first-byte probe to jump to candidates.
        private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> pattern, byte[]? foldedPattern)
        {
            if (foldedPattern is null)
            {
                return haystack.IndexOf(pattern);
            }

            int patLen = foldedPattern.Length;
            byte f0 = foldedPattern[0];
            bool firstIsLetter = f0 >= (byte)'a' && f0 <= (byte)'z';
            byte upper0 = (byte)(f0 - 32);
            int searchFrom = 0;
            while (true)
            {
                ReadOnlySpan<byte> window = haystack.Slice(searchFrom);
                int rel = firstIsLetter ? window.IndexOfAny(f0, upper0) : window.IndexOf(f0);
                if (rel < 0)
                {
                    return -1;
                }
                int i = searchFrom + rel;
                if (i + patLen > haystack.Length)
                {
                    return -1;
                }
                int j = 1;
                for (; j < patLen; j++)
                {
                    if (AsciiFold(haystack[i + j]) != foldedPattern[j])
                    {
                        break;
                    }
                }
                if (j == patLen)
                {
                    return i;
                }
                searchFrom = i + 1;
            }
        }

        private static bool IsWordByte(int b)
            => b >= 0 && ((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9') || b == '_');

        // A match is "whole word" when the bytes immediately before and after it are non-word bytes or the
        // file edge. Neighbours come from the buffer when present, else from a one-byte source read (only at
        // a buffer edge), so the check is correct across chunk boundaries.
        private bool IsWholeWordMatch(byte[] buffer, int available, long bufferBase, int matchAt, int patLen)
        {
            int left = matchAt > 0 ? buffer[matchAt - 1] : ReadByteAt(bufferBase + matchAt - 1);
            int rightIdx = matchAt + patLen;
            int right = rightIdx < available ? buffer[rightIdx] : ReadByteAt(bufferBase + rightIdx);
            return !IsWordByte(left) && !IsWordByte(right);
        }

        private int ReadByteAt(long offset)
        {
            if (offset < 0 || offset >= source.Length)
            {
                return -1;
            }
            Span<byte> one = stackalloc byte[1];
            return source.Read(offset, one) == 1 ? one[0] : -1;
        }

        private async ValueTask<int> ReadFullAsync(long offset, Memory<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await source.ReadAsync(offset + total, buffer.Slice(total));
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
            return total;
        }
    }
}
