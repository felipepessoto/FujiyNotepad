using FujiyNotepad.Core;
using FujiyNotepad.Presentation;

namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the word-wrap paths of <see cref="TextLayoutEngine"/> (issue #31): the wrapped render model,
    /// continuation-row flags, display-row caret movement, hit-testing, and that wrap is fully off by default.
    /// Metrics are a fixed 10x20 cell; at viewport width 100 the wrap budget is 8 columns
    /// (floor((100 - TextPadding 8 - TextPadding 8) / 10)).
    /// </summary>
    public class EngineWordWrapTests
    {
        private static async Task<TextLayoutEngine> NewEngineAsync(
            string content, double cw = 10, double lh = 20, double vw = 100, double vh = 200)
        {
            LineProvider provider = await TestData.BuildProviderAsync(content);
            var engine = new TextLayoutEngine { ViewportWidth = vw, ViewportHeight = vh };
            engine.SetMetrics(cw, lh);
            engine.SetProvider(provider);
            return engine;
        }

        [Fact]
        public void WordWrap_IsOffByDefault()
        {
            Assert.False(new TextLayoutEngine().WordWrap);
        }

        [Fact]
        public async Task WordWrap_Off_RendersOneRowPerSourceLine()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDEFGHIJKLMNOPQRST", 3));

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(false, false);

            Assert.All(lines, l => Assert.False(l.IsWrapContinuation));
            Assert.Equal("ABCDEFGHIJKLMNOPQRST", lines[0].Display); // full line, not wrapped
        }

        [Fact]
        public async Task WordWrap_On_WrapsLongLineIntoRows()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDEFGHIJKLMNOPQRST\n"); // 20 chars, budget 8
            e.WordWrap = true;

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(false, false);

            Assert.Equal(3, lines.Count);
            Assert.Equal("ABCDEFGH", lines[0].Display);
            Assert.Equal("IJKLMNOP", lines[1].Display);
            Assert.Equal("QRST", lines[2].Display);
            Assert.All(lines, l => Assert.Equal(0, l.LineIndex)); // all the same source line
        }

        [Fact]
        public async Task WordWrap_ContinuationRowsAreFlagged()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDEFGHIJKLMNOPQRST\n");
            e.WordWrap = true;

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(false, false);

            Assert.False(lines[0].IsWrapContinuation); // first row carries the gutter number
            Assert.True(lines[1].IsWrapContinuation);
            Assert.True(lines[2].IsWrapContinuation);
        }

        [Fact]
        public async Task WordWrap_RowsAreLaidOutDownwardByLineHeight()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDEFGHIJKLMNOPQRST\nsecond\n");
            e.WordWrap = true;

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(false, false);

            Assert.Equal(0.0, lines[0].Y);
            Assert.Equal(20.0, lines[1].Y);
            Assert.Equal(40.0, lines[2].Y);
            Assert.Equal(60.0, lines[3].Y);      // the next source line follows the 3 wrapped rows
            Assert.Equal(1, lines[3].LineIndex);
            Assert.Equal("second", lines[3].Display);
        }

        [Fact]
        public async Task WordWrap_CaretDownMovesByDisplayRowWithinAWrappedLine()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDEFGHIJKLMNOPQRST\n");
            e.WordWrap = true;

            // Caret starts at (0,0); Down should land on the next wrapped row of the SAME source line.
            e.HandleKey(NavKey.Down, shift: false);

            Assert.Equal(0, e.CaretPosition.Line);
            Assert.Equal(8, e.CaretPosition.Column); // start of the 2nd wrapped row (column 8)
        }

        [Fact]
        public async Task WordWrap_CaretDownKeepsVisualColumn()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDEFGHIJKLMNOPQRST\n");
            e.WordWrap = true;
            e.GoToLineColumn(0, 3); // visual column 3 in row 0

            e.HandleKey(NavKey.Down, shift: false);

            Assert.Equal(11, e.CaretPosition.Column); // row 1 starts at 8, +3 = 11
        }

        [Fact]
        public async Task WordWrap_HitTestMapsYToWrappedRow()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDEFGHIJKLMNOPQRST\n");
            e.WordWrap = true;

            // y in the 2nd display row (lineHeight 20 → row 1 spans y[20,40)); x near column 2 of that row.
            TextPosition pos = e.HitTest(TextLayoutEngine.TextPadding + 2 * 10 + 1, 25);

            Assert.Equal(0, pos.Line);
            Assert.Equal(10, pos.Column); // row 1 start (8) + 2
        }

        [Fact]
        public async Task WordWrap_TogglingBackToOff_RestoresFullLine()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDEFGHIJKLMNOPQRST\n");
            e.WordWrap = true;
            e.WordWrap = false;

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(false, false);

            Assert.Equal("ABCDEFGHIJKLMNOPQRST", lines[0].Display);
            Assert.False(lines[0].IsWrapContinuation);
        }
    }
}
