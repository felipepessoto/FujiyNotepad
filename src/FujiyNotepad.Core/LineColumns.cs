using System.Text;

namespace FujiyNotepad.Core
{
    /// <summary>
    /// Maps a source line to its on-screen representation under a fixed tab size: the display string
    /// (tabs expanded to spaces) plus a column map so character positions and pixel positions can be
    /// converted both ways for rendering, hit-testing, and selection. Assumes a monospace font, so one
    /// display column equals one character cell.
    /// </summary>
    public sealed class LineColumns
    {
        /// <summary>The original line text (tabs preserved); used for selection and copy.</summary>
        public string Source { get; }

        /// <summary>The line with tabs expanded to spaces; used for rendering.</summary>
        public string Display { get; }

        // columnAt[i] = display column at which source character i begins; columnAt[Source.Length] is the
        // total display width. Non-decreasing, advancing by 1 per normal char and to the next tab stop
        // per tab.
        private readonly int[] columnAt;

        private LineColumns(string source, string display, int[] columnAt)
        {
            Source = source;
            Display = display;
            this.columnAt = columnAt;
        }

        public int TotalColumns => columnAt[Source.Length];

        public static LineColumns Build(string source, int tabSize)
        {
            if (tabSize < 1)
            {
                tabSize = 1;
            }

            int n = source.Length;
            var columnAt = new int[n + 1];
            var display = new StringBuilder(n);

            int col = 0;
            for (int i = 0; i < n; i++)
            {
                columnAt[i] = col;
                char c = source[i];
                if (c == '\t')
                {
                    int advance = tabSize - (col % tabSize);
                    col += advance;
                    display.Append(' ', advance);
                }
                else
                {
                    col += IsWide(c) ? 2 : 1;
                    display.Append(c);
                }
            }
            columnAt[n] = col;

            return new LineColumns(source, display.ToString(), columnAt);
        }

        // East Asian Wide / Fullwidth code points (BMP) that a monospace font renders two cells wide:
        // CJK ideographs, kana, Hangul, fullwidth forms, etc. Surrogate-pair code points (e.g. most emoji
        // and CJK Ext-B) are treated as one cell per code unit and are not handled here.
        private static bool IsWide(char c) =>
            (c >= '\u1100' && c <= '\u115F') || // Hangul Jamo
            (c >= '\u2E80' && c <= '\u303E') || // CJK radicals / Kangxi / CJK symbols
            (c >= '\u3041' && c <= '\u33FF') || // Hiragana, Katakana, CJK symbols & punctuation, etc.
            (c >= '\u3400' && c <= '\u4DBF') || // CJK Unified Ideographs Extension A
            (c >= '\u4E00' && c <= '\u9FFF') || // CJK Unified Ideographs
            (c >= '\uA000' && c <= '\uA4CF') || // Yi syllables
            (c >= '\uAC00' && c <= '\uD7A3') || // Hangul syllables
            (c >= '\uF900' && c <= '\uFAFF') || // CJK Compatibility Ideographs
            (c >= '\uFE30' && c <= '\uFE4F') || // CJK Compatibility Forms
            (c >= '\uFF00' && c <= '\uFF60') || // Fullwidth forms
            (c >= '\uFFE0' && c <= '\uFFE6');    // Fullwidth signs

        /// <summary>Display column where the caret sits before source character <paramref name="charIndex"/>.</summary>
        public int ColumnOfCharIndex(int charIndex)
        {
            if (charIndex <= 0)
            {
                return 0;
            }
            if (charIndex >= columnAt.Length)
            {
                charIndex = columnAt.Length - 1;
            }
            return columnAt[charIndex];
        }

        /// <summary>
        /// Source character index whose caret position is nearest to display column
        /// <paramref name="column"/> (used to turn a clicked x-position into a caret location).
        /// </summary>
        public int CharIndexOfColumn(double column)
        {
            if (column <= 0)
            {
                return 0;
            }
            int last = Source.Length;
            if (column >= columnAt[last])
            {
                return last;
            }

            // Binary search for the first boundary at or past the target column, then pick the nearer
            // of it and its predecessor.
            int lo = 0;
            int hi = last;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (columnAt[mid] < column)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            if (lo > 0 && (column - columnAt[lo - 1]) <= (columnAt[lo] - column))
            {
                return lo - 1;
            }
            return lo;
        }
    }
}
