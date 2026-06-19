using System.Text;

namespace FujiyNotepad.Core
{
    /// <summary>
    /// A compact, dependency-free Windows-1252 ("ANSI") codec. .NET Core doesn't ship code-page encodings
    /// without the heavyweight <c>System.Text.Encoding.CodePages</c> provider (which would bloat the slim
    /// Native-AOT build), so this single-byte encoding is hand-rolled: 0x00–0x7F is ASCII, 0xA0–0xFF is
    /// Latin-1 (identity), and the 0x80–0x9F range uses the Windows-1252 punctuation table (smart quotes,
    /// dashes, the euro sign, …). Bytes Windows-1252 leaves undefined pass through to the same code point.
    /// </summary>
    public sealed class Windows1252Encoding : Encoding
    {
        public static readonly Windows1252Encoding Instance = new();

        // 0x80–0x9F -> Unicode. Undefined slots (0x81/0x8D/0x8F/0x90/0x9D) map to their own code point.
        private static readonly char[] HighMap =
        {
            '\u20AC', '\u0081', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
            '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u008D', '\u017D', '\u008F',
            '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
            '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u009D', '\u017E', '\u0178',
        };

        private static readonly Dictionary<char, byte> ReverseHigh = BuildReverse();

        private static Dictionary<char, byte> BuildReverse()
        {
            var map = new Dictionary<char, byte>(HighMap.Length);
            for (int i = 0; i < HighMap.Length; i++)
            {
                map[HighMap[i]] = (byte)(0x80 + i);
            }
            return map;
        }

        private static char ToChar(byte b) => b >= 0x80 && b <= 0x9F ? HighMap[b - 0x80] : (char)b;

        private static byte ToByte(char c)
        {
            if (c < 0x80 || (c >= 0xA0 && c <= 0xFF))
            {
                return (byte)c;
            }
            return ReverseHigh.TryGetValue(c, out byte b) ? b : (byte)'?';
        }

        public override int GetByteCount(char[] chars, int index, int count) => count;

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            for (int i = 0; i < charCount; i++)
            {
                bytes[byteIndex + i] = ToByte(chars[charIndex + i]);
            }
            return charCount;
        }

        public override int GetCharCount(byte[] bytes, int index, int count) => count;

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            for (int i = 0; i < byteCount; i++)
            {
                chars[charIndex + i] = ToChar(bytes[byteIndex + i]);
            }
            return byteCount;
        }

        public override int GetMaxByteCount(int charCount) => charCount;

        public override int GetMaxCharCount(int byteCount) => byteCount;

        // Every byte maps to exactly one character (and vice versa), so character count == byte count. The
        // viewer uses this to show a huge file's character count instantly, without a full decode pass (#39).
        public override bool IsSingleByte => true;
    }
}
