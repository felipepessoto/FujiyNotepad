using FujiyNotepad.Core;

namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the selection-occurrence highlight channel (issue #130) in the render model: it paints every
    /// occurrence of the selected term via <see cref="VisibleLine.SelectionMatches"/>, coexists with an active
    /// Find (a different selected word still highlights, while occurrences sitting on a Find match are excluded
    /// to avoid double-painting), and the engine slices the single-line selected text.
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
        public async Task SelectionOccurrence_OnTheFindTerm_IsExcludedToAvoidDoublePaint()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDE\nABCDE");
            e.SetSelectionHighlighter(new LiteralLineHighlighter("ABC", ignoreCase: false, wholeWord: false));
            e.SetHighlighter(new LiteralLineHighlighter("ABC", ignoreCase: false, wholeWord: false));

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: false, caretVisible: false);

            // Selecting the searched word: Find paints it, and the selection-occurrence span coincides with the
            // Find match, so it is excluded — no second colour over the Find highlight.
            Assert.Single(lines[0].Matches);
            Assert.Empty(lines[0].SelectionMatches);
        }

        [Fact]
        public async Task SelectionOccurrence_OfADifferentWord_CoexistsWithFind()
        {
            // "ABC" at [0,3], "XY" at [3,2] on each line.
            TextLayoutEngine e = await NewEngineAsync("ABCXY\nABCXY");
            e.SetHighlighter(new LiteralLineHighlighter("ABC", ignoreCase: false, wholeWord: false));      // searching ABC
            e.SetSelectionHighlighter(new LiteralLineHighlighter("XY", ignoreCase: false, wholeWord: false)); // selected XY

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: false, caretVisible: false);

            // Both highlight: Find paints "ABC", and the differently-selected "XY" still paints its occurrences.
            Assert.Single(lines[0].Matches);
            Assert.Equal(TextLayoutEngine.TextPadding, lines[0].Matches[0].X);              // ABC at column 0
            Assert.Single(lines[0].SelectionMatches);
            Assert.Equal(30.0 + TextLayoutEngine.TextPadding, lines[0].SelectionMatches[0].X); // XY at column 3
            Assert.Equal(20.0, lines[0].SelectionMatches[0].Width);                          // 2 chars * 10px
            Assert.Single(lines[1].SelectionMatches);
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
