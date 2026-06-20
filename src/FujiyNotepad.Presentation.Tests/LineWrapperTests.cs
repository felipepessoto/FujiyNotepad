using FujiyNotepad.Core;
using FujiyNotepad.Presentation;

namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the pure word-wrap layout primitive (issue #31): splitting a line into display rows by column
    /// budget, with tabs / double-width characters, the at-least-one-character guarantee, the char→row map,
    /// and slicing the tab-expanded display text per row.
    /// </summary>
    public class LineWrapperTests
    {
        private static LineColumns Cols(string s, int tab = 4) => LineColumns.Build(s, tab);

        [Fact]
        public void Wrap_EmptyLine_IsOneEmptyRow()
        {
            var rows = LineWrapper.Wrap(Cols(""), 10);

            Assert.Single(rows);
            Assert.Equal(0, rows[0].StartChar);
            Assert.Equal(0, rows[0].EndChar);
        }

        [Fact]
        public void Wrap_LineThatFits_IsOneRow()
        {
            var rows = LineWrapper.Wrap(Cols("ABCDE"), 10);

            Assert.Single(rows);
            Assert.Equal(0, rows[0].StartChar);
            Assert.Equal(5, rows[0].EndChar);
        }

        [Fact]
        public void Wrap_LongLine_SplitsAtColumnBudget()
        {
            var rows = LineWrapper.Wrap(Cols("ABCDEFGHIJ"), 4); // 10 chars, width 4

            Assert.Equal(3, rows.Count);
            Assert.Equal((0, 4, 0), (rows[0].StartChar, rows[0].EndChar, rows[0].StartColumn));
            Assert.Equal((4, 8, 4), (rows[1].StartChar, rows[1].EndChar, rows[1].StartColumn));
            Assert.Equal((8, 10, 8), (rows[2].StartChar, rows[2].EndChar, rows[2].StartColumn));
        }

        [Fact]
        public void Wrap_NonPositiveWidth_IsOneRow()
        {
            Assert.Single(LineWrapper.Wrap(Cols("ABCDEFGHIJ"), 0));
        }

        [Fact]
        public void Wrap_CountsDisplayColumnsForTabs()
        {
            // tab = 4 cols (0..3), then 'X' at col 4 → total 5 cols. Width 4 forces a break before 'X'.
            var rows = LineWrapper.Wrap(Cols("\tX"), 4);

            Assert.Equal(2, rows.Count);
            Assert.Equal((0, 1, 0), (rows[0].StartChar, rows[0].EndChar, rows[0].StartColumn));
            Assert.Equal((1, 2, 4), (rows[1].StartChar, rows[1].EndChar, rows[1].StartColumn));
        }

        [Fact]
        public void Wrap_CharWiderThanBudget_StillGetsItsOwnRow()
        {
            // A single tab is 4 columns; at width 2 it cannot fit but must still occupy one row (progress).
            var rows = LineWrapper.Wrap(Cols("\t"), 2);

            Assert.Single(rows);
            Assert.Equal((0, 1), (rows[0].StartChar, rows[0].EndChar));
        }

        [Fact]
        public void Wrap_NoSpuriousTrailingEmptyRow()
        {
            // "AB" at width 1 → [0,1),[1,2); must NOT append a blank [2,2) row.
            var rows = LineWrapper.Wrap(Cols("AB"), 1);

            Assert.Equal(2, rows.Count);
            Assert.Equal((1, 2), (rows[^1].StartChar, rows[^1].EndChar));
        }

        [Fact]
        public void RowCount_MatchesWrap()
        {
            Assert.Equal(3, LineWrapper.RowCount(Cols("ABCDEFGHIJ"), 4));
            Assert.Equal(1, LineWrapper.RowCount(Cols("short"), 80));
        }

        [Fact]
        public void RowOfCharIndex_MapsCaretToRow()
        {
            var rows = LineWrapper.Wrap(Cols("ABCDEFGHIJ"), 4); // [0,4),[4,8),[8,10)

            Assert.Equal(0, LineWrapper.RowOfCharIndex(rows, 0));
            Assert.Equal(0, LineWrapper.RowOfCharIndex(rows, 3));
            Assert.Equal(1, LineWrapper.RowOfCharIndex(rows, 4)); // boundary → later row
            Assert.Equal(2, LineWrapper.RowOfCharIndex(rows, 9));
            Assert.Equal(2, LineWrapper.RowOfCharIndex(rows, 10)); // end-of-line → last row
        }

        [Fact]
        public void DisplaySlice_ReturnsPerRowText()
        {
            LineColumns c = Cols("ABCDEFGHIJ");
            var rows = LineWrapper.Wrap(c, 4);

            Assert.Equal("ABCD", c.DisplaySlice(rows[0].StartChar, rows[0].EndChar));
            Assert.Equal("EFGH", c.DisplaySlice(rows[1].StartChar, rows[1].EndChar));
            Assert.Equal("IJ", c.DisplaySlice(rows[2].StartChar, rows[2].EndChar));
        }

        [Fact]
        public void DisplaySlice_ExpandsTabsToAlignedSpaces()
        {
            LineColumns c = Cols("\tX"); // tab → 4 spaces, then X

            Assert.Equal("    ", c.DisplaySlice(0, 1)); // the tab row
            Assert.Equal("X", c.DisplaySlice(1, 2));
        }
    }
}
