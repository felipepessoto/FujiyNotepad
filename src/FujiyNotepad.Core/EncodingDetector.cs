namespace FujiyNotepad.Core
{
    /// <summary>
    /// Guesses a file's <see cref="TextEncoding"/> from its leading bytes: a byte-order mark when present,
    /// otherwise a heuristic (a high proportion of NUL bytes skewed to odd/even offsets indicates BOM-less
    /// UTF-16 LE/BE; otherwise valid UTF-8 is UTF-8 and anything else falls back to Windows-1252). The user
    /// can always override the guess from the encoding menu.
    /// </summary>
    public static class EncodingDetector
    {
        private const int SampleSize = 4096;

        public static TextEncoding Detect(IByteSource source)
        {
            long length = source.Length;
            if (length == 0)
            {
                return TextEncoding.Utf8;
            }

            int n = (int)Math.Min(SampleSize, length);
            byte[] buffer = new byte[n];
            int read = source.ReadFull(0, buffer);
            return Detect(buffer.AsSpan(0, read));
        }

        public static TextEncoding Detect(ReadOnlySpan<byte> sample)
        {
            // Byte-order marks. Check UTF-32 before UTF-16: the UTF-32 LE BOM starts with the UTF-16 LE BOM.
            if (StartsWith(sample, 0x00, 0x00, 0xFE, 0xFF)) return TextEncoding.Utf32Be;
            if (StartsWith(sample, 0xFF, 0xFE, 0x00, 0x00)) return TextEncoding.Utf32Le;
            if (StartsWith(sample, 0xEF, 0xBB, 0xBF)) return TextEncoding.Utf8Bom;
            if (StartsWith(sample, 0xFE, 0xFF)) return TextEncoding.Utf16Be;
            if (StartsWith(sample, 0xFF, 0xFE)) return TextEncoding.Utf16Le;

            // BOM-less UTF-16: ASCII-ish text alternates a content byte with a NUL high byte, so NULs cluster
            // at odd offsets (LE) or even offsets (BE).
            if (sample.Length >= 4)
            {
                int zerosOdd = 0;
                int zerosEven = 0;
                for (int i = 0; i < sample.Length; i++)
                {
                    if (sample[i] == 0)
                    {
                        if ((i & 1) == 0) zerosEven++; else zerosOdd++;
                    }
                }

                int zeros = zerosOdd + zerosEven;
                if (zeros * 100 / sample.Length >= 20)
                {
                    if (zerosOdd > zerosEven * 4) return TextEncoding.Utf16Le;
                    if (zerosEven > zerosOdd * 4) return TextEncoding.Utf16Be;
                }
            }

            return IsValidUtf8(sample) ? TextEncoding.Utf8 : TextEncoding.Windows1252;
        }

        private static bool StartsWith(ReadOnlySpan<byte> sample, params byte[] prefix)
        {
            if (sample.Length < prefix.Length)
            {
                return false;
            }
            for (int i = 0; i < prefix.Length; i++)
            {
                if (sample[i] != prefix[i])
                {
                    return false;
                }
            }
            return true;
        }

        // Well-formed-UTF-8 check, tolerant of a multi-byte sequence cut off by the sample boundary. Good
        // enough to distinguish UTF-8 from single-byte (Windows-1252/Latin-1) text for auto-detection.
        private static bool IsValidUtf8(ReadOnlySpan<byte> data)
        {
            int i = 0;
            while (i < data.Length)
            {
                byte b = data[i];
                int extra;
                if (b <= 0x7F) { i++; continue; }
                else if (b >= 0xC2 && b <= 0xDF) extra = 1;
                else if (b >= 0xE0 && b <= 0xEF) extra = 2;
                else if (b >= 0xF0 && b <= 0xF4) extra = 3;
                else return false; // stray continuation byte or an invalid lead byte

                if (i + extra >= data.Length)
                {
                    return true; // the sequence is truncated by the sample end; treat as valid
                }
                for (int k = 1; k <= extra; k++)
                {
                    if ((data[i + k] & 0xC0) != 0x80)
                    {
                        return false;
                    }
                }
                i += extra + 1;
            }
            return true;
        }
    }
}
