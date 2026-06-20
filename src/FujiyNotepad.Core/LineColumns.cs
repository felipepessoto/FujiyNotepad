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
        // per tab. Null for an "identity" line (no tabs, no wide chars), where column == char index.
        private readonly int[]? columnAt;

        // displayIndexAt[i] = index into Display where source character i's expansion begins;
        // displayIndexAt[Source.Length] == Display.Length. Differs from columnAt only because a wide char
        // occupies two columns but a single Display char, and a tab occupies several columns and that many
        // Display spaces. Lets a wrapped row slice the Display string for an arbitrary source-char range.
        // Null for an identity line, where Display == Source and displayIndexAt[i] == i.
        private readonly int[]? displayIndexAt;

        private LineColumns(string source, string display, int[]? columnAt, int[]? displayIndexAt)
        {
            Source = source;
            Display = display;
            this.columnAt = columnAt;
            this.displayIndexAt = displayIndexAt;
        }

        public int TotalColumns => columnAt is null ? Source.Length : columnAt[Source.Length];

        /// <summary>
        /// The tab-expanded Display text for the source-char range <c>[startChar, endChar)</c> — the rendered
        /// text of one wrapped row. Tab stops stay aligned to the whole line (the expansion was computed once).
        /// </summary>
        public string DisplaySlice(int startChar, int endChar)
        {
            startChar = Math.Clamp(startChar, 0, Source.Length);
            endChar = Math.Clamp(endChar, startChar, Source.Length);
            if (displayIndexAt is null)
            {
                // Identity line: Display == Source, so the source-char range slices the display directly.
                return Source.Substring(startChar, endChar - startChar);
            }
            int from = displayIndexAt[startChar];
            int to = displayIndexAt[endChar];
            return Display.Substring(from, to - from);
        }

        public static LineColumns Build(string source, int tabSize)
        {
            if (tabSize < 1)
            {
                tabSize = 1;
            }

            if (IsIdentity(source))
            {
                // Fast path for the overwhelmingly common line that has no tabs and no double-width characters:
                // its display form IS the source (no copy) and every column map is the identity i -> i. Skipping
                // the two int maps, the StringBuilder and the display-string copy cuts the per-line allocation
                // (~1.4 KB for a 100-char line) to zero — this runs per newly-revealed line while scrolling. The
                // accessors treat null maps as the identity mapping.
                return new LineColumns(source, source, null, null);
            }

            int n = source.Length;
            var columnAt = new int[n + 1];
            var displayIndexAt = new int[n + 1];
            var display = new StringBuilder(n);

            int col = 0;
            for (int i = 0; i < n; i++)
            {
                columnAt[i] = col;
                displayIndexAt[i] = display.Length;
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
            displayIndexAt[n] = display.Length;

            return new LineColumns(source, display.ToString(), columnAt, displayIndexAt);
        }

        // True when the line needs no tab expansion and has no double-width character, so its display form
        // equals the source and both column maps are the identity. An allocation-free scan; the common case.
        private static bool IsIdentity(string source)
        {
            foreach (char c in source)
            {
                if (c == '\t' || IsWide(c))
                {
                    return false;
                }
            }
            return true;
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
            if (columnAt is null)
            {
                // Identity line: the display column equals the char index, clamped to the line length.
                return Math.Min(charIndex, Source.Length);
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
            if (column <= 0 || double.IsNaN(column))
            {
                return 0;
            }
            int last = Source.Length;
            if (columnAt is null)
            {
                // Identity line: boundaries sit at every integer column 0..last; pick the nearest source
                // character, breaking ties toward the lower index (matching the general path below).
                if (column >= last)
                {
                    return last;
                }
                int i = (int)column;
                return (column - i) <= (i + 1 - column) ? i : i + 1;
            }
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
