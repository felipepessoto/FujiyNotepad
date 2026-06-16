namespace FujiyNotepad.Core
{
    /// <summary>The newline convention a file uses, for the status-bar indicator.</summary>
    public enum LineEnding
    {
        /// <summary>No newline found in the inspected sample (e.g. a single-line or binary file).</summary>
        None,

        /// <summary>Unix line endings: bare <c>\n</c>.</summary>
        Lf,

        /// <summary>Windows line endings: <c>\r\n</c>.</summary>
        CrLf,

        /// <summary>Both <c>\n</c> and <c>\r\n</c> occur in the sample.</summary>
        Mixed,
    }

    /// <summary>
    /// Guesses a file's line-ending convention (LF / CRLF / mixed) from a leading sample, in the file's
    /// <see cref="TextEncoding"/> so the encoded <c>\n</c>/<c>\r</c> sequences of UTF-16/UTF-32 are matched
    /// on code-unit boundaries. Like the encoding guess it is a cheap, best-effort hint based on the start of
    /// the file, not a whole-file scan.
    /// </summary>
    public static class LineEndingDetector
    {
        private const int SampleSize = 256 * 1024;

        public static LineEnding Detect(IByteSource source, TextEncoding encoding)
        {
            long length = source.Length;
            if (length == 0 || encoding.NewLineBytes.Length == 0)
            {
                return LineEnding.None;
            }

            int n = (int)Math.Min(SampleSize, length);
            byte[] buffer = new byte[n];
            int read = source.ReadFull(0, buffer);
            return Detect(buffer.AsSpan(0, read), encoding);
        }

        public static LineEnding Detect(ReadOnlySpan<byte> sample, TextEncoding encoding)
        {
            byte[] nl = encoding.NewLineBytes;
            byte[] cr = encoding.CarriageReturnBytes;
            if (nl.Length == 0)
            {
                return LineEnding.None;
            }

            int unit = Math.Max(1, encoding.CodeUnitSize);
            bool sawLf = false;
            bool sawCrLf = false;

            int from = 0;
            while (from <= sample.Length - nl.Length)
            {
                int idx = sample.Slice(from).IndexOf(nl);
                if (idx < 0)
                {
                    break;
                }
                int at = from + idx;

                // Only count a newline that lands on a code-unit boundary, so a byte pattern straddling two
                // characters in a multi-byte encoding is not mistaken for one (mirrors the line indexer).
                if (at % unit == 0)
                {
                    bool isCrLf = cr.Length > 0
                        && at >= cr.Length
                        && sample.Slice(at - cr.Length, cr.Length).SequenceEqual(cr);
                    if (isCrLf)
                    {
                        sawCrLf = true;
                    }
                    else
                    {
                        sawLf = true;
                    }

                    if (sawLf && sawCrLf)
                    {
                        return LineEnding.Mixed;
                    }
                }

                from = at + nl.Length;
            }

            if (sawCrLf)
            {
                return LineEnding.CrLf;
            }
            if (sawLf)
            {
                return LineEnding.Lf;
            }
            return LineEnding.None;
        }

        /// <summary>The short status-bar label for a line ending, or empty when none was found.</summary>
        public static string ToLabel(LineEnding lineEnding) => lineEnding switch
        {
            LineEnding.Lf => "LF",
            LineEnding.CrLf => "CRLF",
            LineEnding.Mixed => "Mixed",
            _ => string.Empty,
        };
    }
}
