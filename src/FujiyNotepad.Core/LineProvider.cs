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
    public sealed class LineProvider : ILineSource
    {
        // Cap the bytes decoded for any one line so a pathologically long line cannot allocate without
        // bound; the remainder is elided with a marker (this is a viewer, not an editor).
        private const int MaxLineBytes = 64 * 1024;
        private const int MaxCachedLines = 8192;

        private readonly IByteSource source;
        private readonly LineIndexer indexer;
        private readonly TextEncoding encoding;
        private readonly long fileSize;
        private readonly bool endsWithNewline;
        private readonly Dictionary<int, string> cache = new();

        public LineProvider(IByteSource source, LineIndexer indexer, TextEncoding? encoding = null)
        {
            this.source = source;
            this.indexer = indexer;
            this.encoding = encoding ?? TextEncoding.Utf8;
            fileSize = source.Length;
            endsWithNewline = FileEndsWithNewline();
        }

        private bool FileEndsWithNewline()
        {
            byte[] newline = encoding.NewLineBytes;
            if (fileSize < newline.Length)
            {
                return false;
            }
            byte[] tail = new byte[newline.Length];
            if (source.ReadFull(fileSize - newline.Length, tail) != newline.Length)
            {
                return false;
            }
            return tail.AsSpan().SequenceEqual(newline);
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
                // Drop the line's own terminator (the encoded "\n", and a preceding "\r") from the rendered text.
                textLen = StripSuffix(buffer, textLen, encoding.NewLineBytes);
                textLen = StripSuffix(buffer, textLen, encoding.CarriageReturnBytes);
            }

            // Strip a byte-order mark at the very start of the file so it is not rendered on the first line.
            int from = lineIndex == 0 ? BomLength(buffer, textLen) : 0;

            string text = encoding.Encoding.GetString(buffer, from, textLen - from);
            return truncated ? text + " …" : text;
        }

        // Removes a trailing byte sequence (a newline or carriage-return) from the line if present.
        private static int StripSuffix(byte[] buffer, int length, byte[] suffix)
        {
            if (suffix.Length == 0 || length < suffix.Length)
            {
                return length;
            }
            if (!buffer.AsSpan(length - suffix.Length, suffix.Length).SequenceEqual(suffix))
            {
                return length;
            }
            return length - suffix.Length;
        }

        // Length of this encoding's BOM when the buffer begins with it (only relevant on line 0), else 0.
        private int BomLength(byte[] buffer, int available)
        {
            byte[] bom = encoding.Bom;
            if (bom.Length == 0 || available < bom.Length)
            {
                return 0;
            }
            return buffer.AsSpan(0, bom.Length).SequenceEqual(bom) ? bom.Length : 0;
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
            // Never decode more than one capped line's worth. A Find match far into a not-yet-indexed line
            // (or a single giant line) makes byteColumn/available huge; without this cap the allocation and
            // read below would be proportional to that distance — up to gigabytes on the calling thread.
            // The result is clamped to the (also-capped) rendered line length, so this matches GetLine.
            long count = Math.Min(Math.Min(byteColumn, available), MaxLineBytes);
            if (count <= 0)
            {
                return 0;
            }

            byte[] buffer = new byte[count];
            int read = source.ReadFull(start, buffer);

            int from = lineIndex == 0 ? BomLength(buffer, read) : 0;

            // Count whole characters in the prefix; the decoder ignores a trailing partial code unit.
            int chars = encoding.Encoding.GetCharCount(buffer, from, read - from);
            int lineLength = GetLine(lineIndex).Length;
            return Math.Min(chars, lineLength);
        }

        /// <summary>
        /// Converts a character column within <paramref name="lineIndex"/> to a byte offset measured from
        /// the start of that line (the inverse of <see cref="ByteColumnToCharColumn"/>), used to start a
        /// Find from the caret. Counts the encoded bytes of the decoded line prefix; a leading BOM on line 0
        /// is not added, so a Find may begin a couple of bytes early there (harmless - it only widens the
        /// scan).
        /// </summary>
        public long CharColumnToByteColumn(int lineIndex, int charColumn)
        {
            if (charColumn <= 0)
            {
                return 0;
            }

            string text = GetLine(lineIndex);
            int chars = Math.Min(charColumn, text.Length);
            return encoding.Encoding.GetByteCount(text.AsSpan(0, chars));
        }
    }
}
