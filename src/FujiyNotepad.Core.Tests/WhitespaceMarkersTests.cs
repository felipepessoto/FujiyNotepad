using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests
{
    /// <summary>Tests the whitespace/control marker computation (positions, tab width, trailing flag).</summary>
    public class WhitespaceMarkersTests
    {
        private static IReadOnlyList<WhitespaceMarker> Compute(string source, int tabSize = 4) =>
            WhitespaceMarkers.Compute(LineColumns.Build(source, tabSize));

        [Fact]
        public void NoWhitespace_ReturnsEmpty()
        {
            Assert.Empty(Compute("abcdef"));
        }

        [Fact]
        public void Space_ProducesSpaceMarkerAtItsColumn()
        {
            // "a b" -> space is source index 1, display column 1.
            IReadOnlyList<WhitespaceMarker> m = Compute("a b");

            WhitespaceMarker marker = Assert.Single(m);
            Assert.Equal(1, marker.Column);
            Assert.Equal(1, marker.Width);
            Assert.Equal(WhitespaceKind.Space, marker.Kind);
            Assert.False(marker.Trailing);
        }

        [Fact]
        public void Tab_ProducesTabMarkerSpanningExpandedColumns()
        {
            // Leading tab with tabSize 4 expands columns 0..4 -> width 4.
            IReadOnlyList<WhitespaceMarker> m = Compute("\tx", tabSize: 4);

            WhitespaceMarker marker = Assert.Single(m);
            Assert.Equal(0, marker.Column);
            Assert.Equal(4, marker.Width);
            Assert.Equal(WhitespaceKind.Tab, marker.Kind);
        }

        [Fact]
        public void Tab_MidLine_AdvancesToNextTabStop()
        {
            // "ab\t" : 'a','b' at cols 0,1; tab at col 2 advances to col 4 -> width 2.
            IReadOnlyList<WhitespaceMarker> m = Compute("ab\tc", tabSize: 4);

            WhitespaceMarker tab = m.Single(x => x.Kind == WhitespaceKind.Tab);
            Assert.Equal(2, tab.Column);
            Assert.Equal(2, tab.Width);
        }

        [Fact]
        public void TrailingSpacesAndTabs_AreFlagged()
        {
            // "a \t" : the space (idx1) and tab (idx2) are after the last significant char (idx0).
            IReadOnlyList<WhitespaceMarker> m = Compute("a \t", tabSize: 4);

            Assert.Equal(2, m.Count);
            Assert.All(m, marker => Assert.True(marker.Trailing));
        }

        [Fact]
        public void InteriorSpace_IsNotTrailing()
        {
            // "a b " : the interior space (idx1) is not trailing; the final space (idx3) is.
            IReadOnlyList<WhitespaceMarker> m = Compute("a b ");

            Assert.False(m[0].Trailing); // column 1
            Assert.True(m[1].Trailing);  // column 3
        }

        [Fact]
        public void AllBlankLine_IsEntirelyTrailing()
        {
            IReadOnlyList<WhitespaceMarker> m = Compute("   ");

            Assert.Equal(3, m.Count);
            Assert.All(m, marker => Assert.True(marker.Trailing));
        }

        [Fact]
        public void ControlChar_ProducesControlMarker_NotTrailing()
        {
            // A control char (e.g. a stray CR kept mid-line) gets a Control marker.
            IReadOnlyList<WhitespaceMarker> m = Compute("a\u0007b"); // BEL

            WhitespaceMarker marker = Assert.Single(m);
            Assert.Equal(WhitespaceKind.Control, marker.Kind);
            Assert.False(marker.Trailing);
        }
    }
}
