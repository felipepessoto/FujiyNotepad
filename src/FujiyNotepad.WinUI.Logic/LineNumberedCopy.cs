using System.Text;

namespace FujiyNotepad.WinUI.Logic
{
    /// <summary>
    /// Formats lines with their 1-based line numbers for the "Copy with Line Numbers" command. The numbers are
    /// right-aligned to the width of the largest number in the range so the text columns line up. Pure, so it is
    /// headlessly unit-testable.
    /// </summary>
    public static class LineNumberedCopy
    {
        /// <summary>
        /// Renders <paramref name="lines"/> as <c>"&lt;n&gt;: &lt;text&gt;"</c> rows joined by CRLF, where the
        /// first line is numbered <paramref name="firstLineNumber"/> (1-based). Returns an empty string when
        /// there are no lines.
        /// </summary>
        public static string Format(int firstLineNumber, IReadOnlyList<string> lines)
        {
            if (lines.Count == 0)
            {
                return string.Empty;
            }

            int lastNumber = firstLineNumber + lines.Count - 1;
            int width = lastNumber.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;

            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                int number = firstLineNumber + i;
                sb.Append(number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width))
                  .Append(": ")
                  .Append(lines[i]);
                if (i < lines.Count - 1)
                {
                    sb.Append("\r\n");
                }
            }
            return sb.ToString();
        }
    }
}
