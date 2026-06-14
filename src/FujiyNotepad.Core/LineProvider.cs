using System.Text;

namespace FujiyNotepad.Core
{
    /// <summary>
    /// Supplies the decoded text of individual file lines on demand, reading only the requested line
    /// from the underlying <see cref="IByteSource"/> using line boundaries from <see cref="LineIndexer"/>.
    /// A bounded cache keeps recently read lines so repeated rendering of the visible window does not
    /// re-read the disk. Line text never changes once its line is indexed (the index is append-only and
    /// only fully-terminated lines are exposed), so cached values stay valid.
    /// </summary>
    public sealed class LineProvider
    {
        // Cap the bytes decoded for any one line so a pathologically long line cannot allocate without
        // bound; the remainder is elided with a marker (this is a viewer, not an editor).
        private const int MaxLineBytes = 64 * 1024;
        private const int MaxCachedLines = 8192;

        private readonly IByteSource source;
        private readonly LineIndexer indexer;
        private readonly long fileSize;
        private readonly bool endsWithNewline;
        private readonly Dictionary<int, string> cache = new();

        public LineProvider(IByteSource source, LineIndexer indexer)
        {
            this.source = source;
            this.indexer = indexer;
            fileSize = source.Length;
            endsWithNewline = fileSize > 0 && ReadByteAt(fileSize - 1) == (byte)'\n';
        }

        private byte ReadByteAt(long offset)
        {
            Span<byte> one = stackalloc byte[1];
            return source.ReadFull(offset, one) == 1 ? one[0] : (byte)0;
        }

        /// <summary>
        /// Number of lines currently available to display. While indexing is in progress only lines
        /// whose terminating offset is already known are exposed; once indexing completes, the final
        /// unterminated line (a file not ending in '\n') is included. A single trailing newline does not
        /// add a phantom empty line, matching <c>StreamReader.ReadLine</c> semantics.
        /// </summary>
        public int LineCount
        {
            get
            {
                if (fileSize == 0)
                {
                    return 0;
                }

                // The index is seeded with two zeros and grows by one entry per newline, so the number
                // of newlines discovered so far is (indexed entries - 2).
                int newlines = Math.Max(0, indexer.GetNumberOfLinesIndexed() - 2);

                if (indexer.IsCompleted)
                {
                    return endsWithNewline ? newlines : newlines + 1;
                }

                return newlines;
            }
        }

        /// <summary>Returns the decoded text of line <paramref name="lineIndex"/> (0-based), without its terminator.</summary>
        public string GetLine(int lineIndex)
        {
            if (cache.TryGetValue(lineIndex, out string? cached))
            {
                return cached;
            }

            string text = ReadLine(lineIndex);

            if (cache.Count >= MaxCachedLines)
            {
                cache.Clear();
            }
            cache[lineIndex] = text;
            return text;
        }

        private (long start, long end) GetByteRange(int lineIndex)
        {
            // 0-based display line i starts at index entry [i+1]; it ends at entry [i+2] when that next
            // start is known, otherwise at end of file.
            long start = indexer.GetOffsetFromLineNumber(lineIndex + 1);
            long end = (lineIndex + 2 < indexer.GetNumberOfLinesIndexed())
                ? indexer.GetOffsetFromLineNumber(lineIndex + 2)
                : fileSize;
            return (start, end);
        }

        private string ReadLine(int lineIndex)
        {
            (long start, long end) = GetByteRange(lineIndex);

            long length = end - start;
            bool truncated = false;
            if (length > MaxLineBytes)
            {
                length = MaxLineBytes;
                truncated = true;
            }

            if (length <= 0)
            {
                return string.Empty;
            }

            byte[] buffer = new byte[length];
            int read = source.ReadFull(start, buffer);

            int textLen = read;
            if (!truncated)
            {
                // Drop the line's own terminator (LF, and a preceding CR) from the rendered text.
                if (textLen > 0 && buffer[textLen - 1] == (byte)'\n') textLen--;
                if (textLen > 0 && buffer[textLen - 1] == (byte)'\r') textLen--;
            }

            int from = 0;
            // Strip a UTF-8 BOM at the very start of the file so it is not rendered on the first line.
            if (lineIndex == 0 && textLen >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            {
                from = 3;
            }

            string text = Encoding.UTF8.GetString(buffer, from, textLen - from);
            return truncated ? text + " …" : text;
        }

        /// <summary>
        /// Converts a byte offset measured from the start of <paramref name="lineIndex"/> into a
        /// character column within the decoded line (used to position a Find match, which is located by
        /// byte offset, onto the character grid). The result is clamped to the decoded line length.
        /// </summary>
        public int ByteColumnToCharColumn(int lineIndex, long byteColumn)
        {
            if (byteColumn <= 0)
            {
                return 0;
            }

            (long start, long end) = GetByteRange(lineIndex);
            long available = end - start;
            long count = Math.Min(byteColumn, available);
            if (count <= 0)
            {
                return 0;
            }

            byte[] buffer = new byte[count];
            int read = source.ReadFull(start, buffer);

            int from = 0;
            if (lineIndex == 0 && read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            {
                from = 3;
            }

            // Count whole UTF-8 characters in the prefix; ignore a trailing partial sequence.
            int chars = Encoding.UTF8.GetCharCount(buffer, from, read - from);
            int lineLength = GetLine(lineIndex).Length;
            return Math.Min(chars, lineLength);
        }
    }
}
