using FujiyNotepad.Core;

namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the selection-occurrence highlight channel (issue #130) in the render model: it paints every
    /// occurrence of the selected term via <see cref="VisibleLine.SelectionMatches"/>, stands down while a Find
    /// highlight is active (Find takes precedence), and the engine slices the single-line selected text.
    /// </summary>
    public class EngineSelectionHighlightTests
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
        public async Task SelectionHighlighter_PaintsOccurrences()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDE\nXYZQQ\nABCDE");
            e.SetSelectionHighlighter(new LiteralLineHighlighter("ABC", ignoreCase: false, wholeWord: false));

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: false, caretVisible: false);

            // "ABC" matches at column 0 on lines 0 and 2; line 1 has no occurrence.
            Assert.Single(lines[0].SelectionMatches);
            Assert.Equal(TextLayoutEngine.TextPadding, lines[0].SelectionMatches[0].X);
            Assert.Equal(30.0, lines[0].SelectionMatches[0].Width); // 3 chars * 10px
            Assert.Empty(lines[1].SelectionMatches);
            Assert.Single(lines[2].SelectionMatches);
        }

        [Fact]
        public async Task SelectionHighlighter_Null_LeavesNoMatches()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDE\nABCDE");
            e.SetSelectionHighlighter(new LiteralLineHighlighter("ABC", ignoreCase: false, wholeWord: false));
            e.SetSelectionHighlighter(null);

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: false, caretVisible: false);

            Assert.Empty(lines[0].SelectionMatches);
        }

        [Fact]
        public async Task FindHighlight_TakesPrecedenceOverSelectionOccurrences()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDE\nABCDE");
            e.SetSelectionHighlighter(new LiteralLineHighlighter("ABC", ignoreCase: false, wholeWord: false));
            e.SetHighlighter(new LiteralLineHighlighter("ABC", ignoreCase: false, wholeWord: false));

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: false, caretVisible: false);

            // Find paints; selection-occurrence stands down so the two never paint at once.
            Assert.Single(lines[0].Matches);
            Assert.Empty(lines[0].SelectionMatches);
        }

        [Fact]
        public async Task SelectionOccurrences_ResumeWhenFindHighlightCleared()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDE\nABCDE");
            e.SetSelectionHighlighter(new LiteralLineHighlighter("ABC", ignoreCase: false, wholeWord: false));
            e.SetHighlighter(new LiteralLineHighlighter("ABC", ignoreCase: false, wholeWord: false));
            e.SetHighlighter(null); // close Find

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: false, caretVisible: false);

            Assert.Empty(lines[0].Matches);
            Assert.Single(lines[0].SelectionMatches);
        }

        [Fact]
        public async Task GetSelectedTextOnSingleLine_ReturnsSelectedSlice()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDE\nFGHIJ");
            e.SelectMatch(0, 1, 3); // columns 1..4 on line 0 -> "BCD"

            Assert.Equal("BCD", e.GetSelectedTextOnSingleLine());
        }

        [Fact]
        public async Task GetSelectedTextOnSingleLine_MultiLineSelection_ReturnsNull()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDE\nFGHIJ");
            e.PointerPress(18, 5, shift: false); // line 0
            e.PointerDrag(28, 25);               // drag down to line 1

            Assert.True(e.HasSelection);
            Assert.Null(e.GetSelectedTextOnSingleLine());
        }

        [Fact]
        public async Task GetSelectedTextOnSingleLine_NoSelection_ReturnsNull()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDE");
            e.SelectMatch(0, 2, 0); // caret only, no selection

            Assert.Null(e.GetSelectedTextOnSingleLine());
        }
    }
}
