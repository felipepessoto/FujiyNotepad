using System.Globalization;

namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Parses a user-entered byte offset for "Go To Offset": a decimal value, or a hexadecimal value with a
    /// <c>0x</c> prefix (case-insensitive). Leading/trailing whitespace is ignored. Pure and unit-testable.
    /// </summary>
    public static class OffsetParser
    {
        /// <summary>
        /// Tries to parse <paramref name="text"/> into a non-fractional byte <paramref name="offset"/>. Returns
        /// <c>false</c> (leaving <paramref name="offset"/> as 0) for null/blank or otherwise unparseable input.
        /// </summary>
        public static bool TryParse(string? text, out long offset)
        {
            offset = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset);
            }

            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset);
        }
    }
}
