using System.Runtime.CompilerServices;

namespace FujiyNotepad.UI.Model
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

        public long FileSize => source.Length;

        public TextSearcher(IByteSource source) : this(source, DefaultChunkSize)
        {
        }

        internal TextSearcher(IByteSource source, int chunkSize)
        {
            this.source = source;
            this.chunkSize = chunkSize;
        }

        /// <summary>
        /// Yields the absolute offset of every occurrence of <paramref name="pattern"/> at or after
        /// <paramref name="startOffset"/> (overlapping matches included, advancing one byte past each).
        /// Cancellation is cooperative: a cancelled <paramref name="token"/> stops enumeration between
        /// chunks <em>without throwing</em>, so callers can treat a cancelled search as an empty result.
        /// </summary>
        public async IAsyncEnumerable<long> Search(long startOffset, byte[] pattern, IProgress<int> progress, [EnumeratorCancellation] CancellationToken token = default)
        {
            if (pattern.Length == 0)
            {
                yield break;
            }

            long length = source.Length;
            if (startOffset < 0)
            {
                startOffset = 0;
            }
            if (startOffset >= length)
            {
                progress.Report(100);
                yield break;
            }

            int overlap = pattern.Length - 1;
            byte[] buffer = new byte[chunkSize + overlap];
            var matches = new List<long>();

            long readPos = startOffset;     // next absolute read position
            int carry = 0;                  // bytes retained at the front of the buffer
            long bufferBase = startOffset;  // absolute offset of buffer[0]
            long total = length - startOffset;
            int lastPercent = -1;
            progress.Report(0);

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

                matches.Clear();
                CollectForward(buffer.AsSpan(0, available), pattern, bufferBase, matches);
                foreach (long match in matches)
                {
                    yield return match;
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

                if (total > 0)
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

            progress.Report(100);
        }

        /// <summary>
        /// Yields the absolute offset of every occurrence of <paramref name="value"/> strictly before
        /// <paramref name="startOffset"/>, in descending order. For '\n' it additionally yields -1 at
        /// the end (the implicit start-of-file line boundary the viewport relies on).
        /// </summary>
        public IEnumerable<long> SearchBackward(long startOffset, byte value, IProgress<int> progress)
        {
            if (startOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startOffset), $"{nameof(startOffset)} cannot be negative");
            }

            progress.Report(0);

            byte[] buffer = new byte[chunkSize];
            var matches = new List<long>();
            long pos = Math.Min(startOffset, source.Length); // scan the range [0, pos)

            while (pos > 0)
            {
                int toRead = (int)Math.Min(chunkSize, pos);
                long blockStart = pos - toRead;
                int read = source.ReadFull(blockStart, buffer.AsSpan(0, toRead));

                matches.Clear();
                CollectBackward(buffer.AsSpan(0, read), value, blockStart, matches);
                foreach (long match in matches)
                {
                    yield return match;
                }

                pos = blockStart;
            }

            if (value == (byte)'\n')
            {
                yield return -1;
            }
        }

        private static void CollectForward(ReadOnlySpan<byte> span, ReadOnlySpan<byte> pattern, long baseOffset, List<long> results)
        {
            int from = 0;
            while (true)
            {
                int idx = span.Slice(from).IndexOf(pattern);
                if (idx < 0)
                {
                    break;
                }
                int matchAt = from + idx;
                results.Add(baseOffset + matchAt);
                from = matchAt + 1;
            }
        }

        private static void CollectBackward(ReadOnlySpan<byte> span, byte value, long baseOffset, List<long> results)
        {
            int end = span.Length;
            int idx;
            while ((idx = span.Slice(0, end).LastIndexOf(value)) >= 0)
            {
                results.Add(baseOffset + idx);
                end = idx;
            }
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
