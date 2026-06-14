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
        /// Yields the absolute offset of every occurrence of <paramref name="pattern"/> at or after
        /// <paramref name="startOffset"/> (overlapping matches included, advancing one byte past each).
        /// Cancellation is cooperative: a cancelled <paramref name="token"/> stops enumeration between
        /// chunks <em>without throwing</em>, so callers can treat a cancelled search as an empty result.
        /// </summary>
        public async IAsyncEnumerable<long> Search(long startOffset, byte[] pattern, IProgress<int>? progress = null, [EnumeratorCancellation] CancellationToken token = default)
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
                progress?.Report(100);
                yield break;
            }

            int overlap = pattern.Length - 1;
            byte[] buffer = new byte[chunkSize + overlap];

            long readPos = startOffset;     // next absolute read position
            int carry = 0;                  // bytes retained at the front of the buffer
            long bufferBase = startOffset;  // absolute offset of buffer[0]
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
                    int idx = buffer.AsSpan(from, available - from).IndexOf(pattern);
                    if (idx < 0)
                    {
                        break;
                    }
                    int matchAt = from + idx;
                    yield return bufferBase + matchAt;
                    from = matchAt + 1;
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
