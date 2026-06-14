using FujiyNotepad.Core;

namespace FujiyNotepad.WinUI.Logic.Tests
{
    /// <summary>
    /// Tests <see cref="TextLayoutEngine.GetVisibleLines"/> — the per-line render model the host paints.
    /// This covers the non-trivial "what to draw" logic (visible range, line Y positions, selection spans,
    /// caret placement, horizontal extent) without any graphics device, so it runs on a normal test host.
    /// </summary>
    public class EngineRenderModelTests
    {
        private static async Task<TextLayoutEngine> NewEngineAsync(
            string content, double cw = 10, double lh = 20, double vw = 100, double vh = 100)
        {
            LineProvider provider = await TestData.BuildProviderAsync(content);
            var engine = new TextLayoutEngine { ViewportWidth = vw, ViewportHeight = vh };
            engine.SetMetrics(cw, lh);
            engine.SetProvider(provider);
            return engine;
        }

        [Fact]
        public async Task GetVisibleLines_ReturnsVisibleRangeWithCorrectLayout()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 100), cw: 10, lh: 20, vw: 100, vh: 100);

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: false, caretVisible: false);

            Assert.Equal(6, lines.Count); // ceil(100/20) + 1
            Assert.Equal(0, lines[0].LineIndex);
            Assert.Equal(0.0, lines[0].Y);
            Assert.Equal("ABCDE", lines[0].Display);
            Assert.Equal(TextLayoutEngine.TextPadding, lines[0].TextX);
            Assert.Equal(5, lines[5].LineIndex);
            Assert.Equal(100.0, lines[5].Y);
        }

        [Fact]
        public async Task GetVisibleLines_AppliesLeftPadding()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDEFGHIJKLMNOPQRST", 5), cw: 10, lh: 20, vw: 100, vh: 100);

            VisibleLine notScrolled = e.GetVisibleLines(hasFocus: false, caretVisible: false)[0];
            // Text is inset by the padding instead of being flush against the left border (x = 0).
            Assert.Equal(TextLayoutEngine.TextPadding, notScrolled.TextX);
            Assert.True(notScrolled.TextX > 0);

            // The inset rides along with the horizontal scroll offset (extent was computed by the call above).
            e.HorizontalOffset = 30;
            VisibleLine scrolled = e.GetVisibleLines(hasFocus: false, caretVisible: false)[0];
            Assert.Equal(TextLayoutEngine.TextPadding - 30, scrolled.TextX);
        }

        [Fact]
        public async Task GetVisibleLines_StartsAtFirstVisibleLine()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 100));
            e.FirstVisibleLine = 10;

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: false, caretVisible: false);

            Assert.Equal(10, lines[0].LineIndex);
            Assert.Equal(0.0, lines[0].Y);
        }

        [Fact]
        public async Task GetVisibleLines_StopsAtEndOfDocument()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3), cw: 10, lh: 20, vh: 200);

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: false, caretVisible: false);

            Assert.Equal(3, lines.Count); // only 3 lines exist even though the viewport could show more
        }

        [Fact]
        public async Task GetVisibleLines_CaretShownOnlyWhenFocusedAndBlinkOn()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 5));
            e.GoToLine(0);

            Assert.True(e.GetVisibleLines(hasFocus: true, caretVisible: true)[0].HasCaret);
            Assert.False(e.GetVisibleLines(hasFocus: false, caretVisible: true)[0].HasCaret);
            Assert.False(e.GetVisibleLines(hasFocus: true, caretVisible: false)[0].HasCaret);
        }

        [Fact]
        public async Task GetVisibleLines_CaretXReflectsColumn()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 5), cw: 10, lh: 20);
            e.SelectMatch(0, 3, 0); // caret at column 3, no selection

            VisibleLine line = e.GetVisibleLines(hasFocus: true, caretVisible: true)[0];

            Assert.True(line.HasCaret);
            Assert.Equal(30.0 + TextLayoutEngine.TextPadding, line.CaretX); // column 3 * 10 px + left padding
            Assert.False(line.HasSelection);
        }

        [Fact]
        public async Task GetVisibleLines_SelectionSpanOnSingleLine()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 5), cw: 10, lh: 20);
            e.SelectMatch(0, 1, 3); // columns 1..4

            VisibleLine line = e.GetVisibleLines(hasFocus: true, caretVisible: true)[0];

            Assert.True(line.HasSelection);
            Assert.Equal(10.0 + TextLayoutEngine.TextPadding, line.SelectionX); // column 1 * 10 + left padding
            Assert.Equal(30.0, line.SelectionWidth); // (4 - 1) * 10
        }

        [Fact]
        public async Task GetVisibleLines_MultiLineSelection_FillsMiddleLine()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3), cw: 10, lh: 20, vw: 200, vh: 200);
            e.PointerPress(25, 5, shift: false); // (0,2)
            e.PointerDrag(35, 45);               // (2,3)

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: true, caretVisible: true);

            // Middle line is fully highlighted: from column 0 to one past the last column (5 + 1).
            Assert.True(lines[1].HasSelection);
            Assert.Equal(TextLayoutEngine.TextPadding, lines[1].SelectionX);
            Assert.Equal(60.0, lines[1].SelectionWidth);
            // First and last lines are partially highlighted.
            Assert.Equal(20.0 + TextLayoutEngine.TextPadding, lines[0].SelectionX); // from column 2 + left padding
            Assert.True(lines[2].HasSelection);
            Assert.Equal(TextLayoutEngine.TextPadding, lines[2].SelectionX);
            Assert.Equal(30.0, lines[2].SelectionWidth); // up to column 3
        }

        [Fact]
        public void GetVisibleLines_WithoutProvider_ReturnsEmptyAndSetsExtentToViewport()
        {
            var e = new TextLayoutEngine { ViewportWidth = 123, ViewportHeight = 100 };
            e.SetMetrics(10, 20);

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: true, caretVisible: true);

            Assert.Empty(lines);
            Assert.Equal(123.0, e.HorizontalExtentPx);
        }

        [Fact]
        public async Task GetVisibleLines_UpdatesHorizontalExtentAndRaisesViewChanged()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDEFGHIJ", 5), cw: 10, lh: 20, vw: 50, vh: 100);
            int viewChanged = 0;
            e.ViewChanged += () => viewChanged++;

            e.GetVisibleLines(hasFocus: false, caretVisible: false);

            Assert.Equal(100.0 + 2 * TextLayoutEngine.TextPadding, e.HorizontalExtentPx); // widest line 10*10 + left/right padding
            Assert.True(viewChanged >= 1);
        }
    }
}
