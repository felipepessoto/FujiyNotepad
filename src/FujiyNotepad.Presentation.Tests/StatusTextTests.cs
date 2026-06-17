namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the status-bar string builder: cursor position, the single/multi-line/uncounted selection
    /// branches, the timestamp-delta suffix, and the line / filter / index / character readouts.
    /// </summary>
    public class StatusTextTests
    {
        [Fact]
        public void CursorStatus_NoSelection_IsPositionOnly()
        {
            string s = StatusText.CursorStatus(0, 0, new SelectionStats(0, 0), null);

            Assert.Equal("Ln 1, Col 1", s);
        }

        [Fact]
        public void CursorStatus_PositionIsOneBased()
        {
            string s = StatusText.CursorStatus(4, 2, new SelectionStats(0, 0), null);

            Assert.Equal("Ln 5, Col 3", s);
        }

        [Fact]
        public void CursorStatus_SingleLineSelection_ShowsCharacters()
        {
            string s = StatusText.CursorStatus(0, 0, new SelectionStats(5, 1), null);

            Assert.Equal("Ln 1, Col 1  (5 selected)", s);
        }

        [Fact]
        public void CursorStatus_MultiLineSelection_ShowsCharactersAndLines()
        {
            string s = StatusText.CursorStatus(0, 0, new SelectionStats(13, 3), null);

            Assert.Equal("Ln 1, Col 1  (13 selected, 3 lines)", s);
        }

        [Fact]
        public void CursorStatus_LargeUncountedSelection_ShowsLinesOnly()
        {
            // Characters == -1 means "too large to count" -> only the line count is shown.
            string s = StatusText.CursorStatus(0, 0, new SelectionStats(-1, 6), null);

            Assert.Equal("Ln 1, Col 1  (6 lines selected)", s);
        }

        [Fact]
        public void CursorStatus_MultiLineWithTimestampDelta_AppendsDelta()
        {
            string s = StatusText.CursorStatus(0, 0, new SelectionStats(13, 3), TimeSpan.FromSeconds(150));

            Assert.Equal("Ln 1, Col 1  (13 selected, 3 lines)  delta = 2m 30s", s);
        }

        [Fact]
        public void CursorStatus_SingleLineIgnoresTimestampDelta()
        {
            // The delta is only meaningful across lines; a single-line selection never shows it.
            string s = StatusText.CursorStatus(0, 0, new SelectionStats(5, 1), TimeSpan.FromSeconds(150));

            Assert.Equal("Ln 1, Col 1  (5 selected)", s);
        }

        [Fact]
        public void LineCount_FormatsCount()
        {
            Assert.Equal("5 lines", StatusText.LineCount(5));
        }

        [Fact]
        public void Filtered_FormatsMatchesOfTotal()
        {
            Assert.Equal("Filtered: 2 of 9 lines", StatusText.Filtered(2, 9));
        }

        [Fact]
        public void IndexProgress_FormatsPercent()
        {
            Assert.Equal("50% indexed", StatusText.IndexProgress(50));
        }

        [Theory]
        [InlineData(-1, "")]
        [InlineData(0, "0 characters")]
        [InlineData(1, "1 character")]
        [InlineData(42, "42 characters")]
        public void CharacterCount_FormatsByMagnitude(long count, string expected)
        {
            Assert.Equal(expected, StatusText.CharacterCount(count));
        }
    }
}
