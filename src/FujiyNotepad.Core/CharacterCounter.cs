using System.Text;

namespace FujiyNotepad.Core
{
    /// <summary>
    /// Counts the total number of decoded characters in a file, reading it in chunks (constant memory) with a
    /// stateful <see cref="Decoder"/> so a multi-byte character that straddles a chunk boundary is counted
    /// exactly once. A leading byte-order mark is skipped. Cancellation is cooperative: a cancelled token stops
    /// the count between chunks and returns the partial total (callers treat a cancelled count as superseded).
    /// </summary>
    public static class CharacterCounter
    {
        private const int DefaultChunkSize = 1 << 20; // 1 MiB

        public static Task<long> CountAsync(IByteSource source, TextEncoding encoding, IProgress<int>? progress = null, CancellationToken token = default)
            => CountAsync(source, encoding, DefaultChunkSize, progress, token);

        internal static async Task<long> CountAsync(IByteSource source, TextEncoding encoding, int chunkSize, IProgress<int>? progress, CancellationToken token)
        {
            long length = source.Length;
            long start = await BomLengthAsync(source, encoding, length);
            if (length <= start)
            {
                progress?.Report(100);
                return 0;
            }

            Decoder decoder = encoding.Encoding.GetDecoder();
            byte[] buffer = new byte[chunkSize];
            long pos = start;
            long total = 0;
            long span = length - start;
            int lastPercent = -1;
            progress?.Report(0);

            while (pos < length)
            {
                if (token.IsCancellationRequested)
                {
                    return total;
                }

                int toRead = (int)Math.Min(chunkSize, length - pos);
                int read = await ReadFullAsync(source, pos, buffer, toRead);
                if (read == 0)
                {
                    break;
                }

                bool flush = pos + read >= length;
                total += decoder.GetCharCount(buffer, 0, read, flush);
                pos += read;

                if (progress != null)
                {
                    int percent = (int)((pos - start) * 100 / span);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(percent);
                    }
                }
            }

            progress?.Report(100);
            return total;
        }

        private static async Task<long> BomLengthAsync(IByteSource source, TextEncoding encoding, long length)
        {
            byte[] bom = encoding.Bom;
            if (bom.Length == 0 || length < bom.Length)
            {
                return 0;
            }

            byte[] head = new byte[bom.Length];
            int read = await ReadFullAsync(source, 0, head, bom.Length);
            if (read < bom.Length)
            {
                return 0;
            }
            return head.AsSpan().SequenceEqual(bom) ? bom.Length : 0;
        }

        private static async Task<int> ReadFullAsync(IByteSource source, long offset, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = await source.ReadAsync(offset + total, buffer.AsMemory(total, count - total));
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
