using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests
{
    public class LineColumnsTests
    {
        [Fact]
        public void PlainAscii_ColumnsAreOneToOne()
        {
            var lc = LineColumns.Build("abc", 4);

            Assert.Equal("abc", lc.Display);
            Assert.Equal(3, lc.TotalColumns);
            Assert.Equal(0, lc.ColumnOfCharIndex(0));
            Assert.Equal(1, lc.ColumnOfCharIndex(1));
            Assert.Equal(3, lc.ColumnOfCharIndex(3));
        }

        [Fact]
        public void LeadingTab_ExpandsToTabStop()
        {
            var lc = LineColumns.Build("\tab", 4);

            Assert.Equal("    ab", lc.Display); // tab at column 0 -> 4 spaces
            Assert.Equal(0, lc.ColumnOfCharIndex(0)); // before the tab
            Assert.Equal(4, lc.ColumnOfCharIndex(1)); // 'a' starts at column 4
            Assert.Equal(5, lc.ColumnOfCharIndex(2)); // 'b'
            Assert.Equal(6, lc.TotalColumns);
        }

        [Fact]
        public void MidLineTab_AdvancesToNextStop()
        {
            var lc = LineColumns.Build("a\tb", 4);

            Assert.Equal("a   b", lc.Display); // 'a' col0, tab fills cols 1..3, 'b' at col4
            Assert.Equal(0, lc.ColumnOfCharIndex(0));
            Assert.Equal(1, lc.ColumnOfCharIndex(1));
            Assert.Equal(4, lc.ColumnOfCharIndex(2));
            Assert.Equal(5, lc.ColumnOfCharIndex(3));
        }

        [Fact]
        public void CharIndexOfColumn_RoundsToNearestBoundary()
        {
            var lc = LineColumns.Build("abc", 4);

            Assert.Equal(0, lc.CharIndexOfColumn(-5));
            Assert.Equal(0, lc.CharIndexOfColumn(0.2));
            Assert.Equal(1, lc.CharIndexOfColumn(0.8));
            Assert.Equal(2, lc.CharIndexOfColumn(2.1));
            Assert.Equal(3, lc.CharIndexOfColumn(100)); // clamped to end
        }

        [Fact]
        public void CharIndexOfColumn_WithinTab_PicksNearerEdge()
        {
            var lc = LineColumns.Build("\tx", 4); // columns: tab spans 0..4, 'x' at 4

            Assert.Equal(0, lc.CharIndexOfColumn(1)); // closer to the tab's start (col 0)
            Assert.Equal(1, lc.CharIndexOfColumn(3)); // closer to 'x' (col 4)
        }

        [Fact]
        public void WideChars_AdvanceTwoColumns()
        {
            // "a中b": 'a' is 1 cell, the CJK ideograph '中' is 2 cells, 'b' is 1 cell.
            var lc = LineColumns.Build("a中b", 4);

            Assert.Equal(4, lc.TotalColumns);
            Assert.Equal(0, lc.ColumnOfCharIndex(0)); // before 'a'
            Assert.Equal(1, lc.ColumnOfCharIndex(1)); // before '中'
            Assert.Equal(3, lc.ColumnOfCharIndex(2)); // before 'b' (after the 2-wide '中')
            Assert.Equal(4, lc.ColumnOfCharIndex(3)); // end
        }

        [Fact]
        public void WideChars_HitTestMapsColumnsToChars()
        {
            var lc = LineColumns.Build("a中b", 4); // columnAt = [0, 1, 3, 4]

            Assert.Equal(0, lc.CharIndexOfColumn(0)); // 'a'
            Assert.Equal(1, lc.CharIndexOfColumn(1)); // '中'
            Assert.Equal(2, lc.CharIndexOfColumn(3)); // 'b'
        }

        [Fact]
        public void FullwidthChars_AreTwoCellsWide()
        {
            // 'Ａ' is U+FF21 (fullwidth Latin A), a 2-cell glyph.
            var lc = LineColumns.Build("Ａb", 4);

            Assert.Equal(3, lc.TotalColumns);
            Assert.Equal(2, lc.ColumnOfCharIndex(1)); // 'b' starts after the 2-wide fullwidth 'Ａ'
        }
    }
}
