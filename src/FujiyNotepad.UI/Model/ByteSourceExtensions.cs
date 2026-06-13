namespace FujiyNotepad.UI.Model
{
    public static class ByteSourceExtensions
    {
        /// <summary>
        /// Reads exactly <paramref name="buffer"/>.Length bytes (or fewer only if end of stream is
        /// reached), looping over partial reads. Returns the total number of bytes read.
        /// </summary>
        public static int ReadFull(this IByteSource source, long offset, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = source.Read(offset + total, buffer.Slice(total));
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
