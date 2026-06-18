using System.Globalization;

namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Parses a user-entered percentage for "Go To Percentage" and maps it to a byte offset. Accepts a decimal
    /// value in [0, 100] with an optional trailing <c>%</c> (e.g. <c>50</c>, <c>50%</c>, <c>33.3</c>); leading
    /// and trailing whitespace is ignored. Pure and unit-testable. Mirrors <see cref="OffsetParser"/>.
    /// </summary>
    public static class PercentParser
    {
        /// <summary>
        /// Tries to parse <paramref name="text"/> into a <paramref name="percent"/> in <c>[0, 100]</c>. Returns
        /// <c>false</c> (leaving <paramref name="percent"/> as 0) for null/blank, non-numeric, NaN/infinity, or
        /// out-of-range input.
        /// </summary>
        public static bool TryParse(string? text, out double percent)
        {
            percent = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim();
            if (text.EndsWith("%", StringComparison.Ordinal))
            {
                text = text[..^1].TrimEnd();
            }

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || double.IsNaN(value) || double.IsInfinity(value)
                || value < 0 || value > 100)
            {
                return false;
            }

            percent = value;
            return true;
        }

        /// <summary>
        /// Maps <paramref name="percent"/> (0-100) of a <paramref name="length"/>-byte file to a byte offset,
        /// clamped to the last byte (<c>[0, length - 1]</c>). Returns 0 for a non-positive length.
        /// </summary>
        public static long ToOffset(double percent, long length)
        {
            if (length <= 0)
            {
                return 0;
            }

            long offset = (long)Math.Round(percent / 100.0 * length, MidpointRounding.AwayFromZero);
            return Math.Clamp(offset, 0, length - 1);
        }
    }
}
