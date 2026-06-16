namespace FujiyNotepad.WinUI.Logic.Tests
{
    /// <summary>Tests the line-index -> scrollbar-row mapping that backs the marker margin.</summary>
    public class ScrollbarMarkersTests
    {
        [Fact]
        public void MapsFirstAndLastLineToTopAndBottomRows()
        {
            IReadOnlyList<int> rows = ScrollbarMarkers.Rows(new[] { 0, 99 }, totalLines: 100, trackHeightPx: 100);

            Assert.Equal(new[] { 0, 99 }, rows);
        }

        [Fact]
        public void MapsMiddleLineToMiddleRow()
        {
            IReadOnlyList<int> rows = ScrollbarMarkers.Rows(new[] { 50 }, totalLines: 101, trackHeightPx: 101);

            Assert.Equal(new[] { 50 }, rows);
        }

        [Fact]
        public void CollapsesDenseLinesToOneRowPerPixel_AndReturnsAscendingDistinct()
        {
            // Many nearby lines on a short track all land on the same handful of rows.
            IReadOnlyList<int> rows = ScrollbarMarkers.Rows(new[] { 0, 1, 2, 3, 999 }, totalLines: 1000, trackHeightPx: 10);

            Assert.Equal(rows.OrderBy(r => r), rows);          // ascending
            Assert.Equal(rows.Distinct(), rows);               // distinct
            Assert.Contains(0, rows);                          // first line -> top
            Assert.Contains(9, rows);                          // last line -> bottom
            Assert.True(rows.Count < 5);                       // dense early lines collapsed
        }

        [Fact]
        public void ClampsOutOfRangeLines()
        {
            IReadOnlyList<int> rows = ScrollbarMarkers.Rows(new[] { -5, 500 }, totalLines: 100, trackHeightPx: 50);

            Assert.Equal(new[] { 0, 49 }, rows); // -5 -> line 0 -> row 0; 500 -> line 99 -> bottom row
        }

        [Fact]
        public void SingleLineDocument_MapsToRowZero()
        {
            Assert.Equal(new[] { 0 }, ScrollbarMarkers.Rows(new[] { 0 }, totalLines: 1, trackHeightPx: 40));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public void EmptyWhenNoTrack(double trackHeight)
        {
            Assert.Empty(ScrollbarMarkers.Rows(new[] { 1, 2 }, totalLines: 100, trackHeightPx: trackHeight));
        }

        [Fact]
        public void EmptyWhenNoLinesOrNoTotal()
        {
            Assert.Empty(ScrollbarMarkers.Rows(System.Array.Empty<int>(), totalLines: 100, trackHeightPx: 100));
            Assert.Empty(ScrollbarMarkers.Rows(new[] { 1 }, totalLines: 0, trackHeightPx: 100));
        }
    }
}
