using FujiyNotepad.Core;

namespace FujiyNotepad.Presentation.Tests
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
        public async Task GetVisibleLines_CaretSharesItsLineTop()
        {
            // The caret is now a composition overlay positioned from its line's top, so the caret's Y must equal
            // that line's top exactly — an exact multiple of the (device-snapped) line height, never a sub-pixel.
            // This locks the vertical contract the overlay relies on for the user's "click the second line" repro.
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 100), cw: 10, lh: 20, vw: 100, vh: 200);
            e.SelectMatch(1, 0, 0); // caret on the second line, no selection

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: true, caretVisible: true);
            VisibleLine caretLine = lines.Single(l => l.HasCaret);

            Assert.Equal(1, caretLine.LineIndex);
            Assert.Equal(20.0, caretLine.Y);                       // 1 * lineHeight
            Assert.Equal(caretLine.LineIndex * 20.0, caretLine.Y); // shares the line's top exactly
        }

        [Fact]
        public async Task GetVisibleLines_CaretYTracksScroll()
        {
            // When the view is scrolled, the caret's Y is measured from the top of the visible range, still landing
            // on an exact line-height multiple, so the overlay tracks the text instead of drifting off the grid.
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 100), cw: 10, lh: 20, vw: 100, vh: 200);
            e.FirstVisibleLine = 10;
            e.SelectMatch(12, 0, 0); // caret two rows below the top of the scrolled view

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: true, caretVisible: true);
            VisibleLine caretLine = lines.Single(l => l.HasCaret);

            Assert.Equal(12, caretLine.LineIndex);
            Assert.Equal(40.0, caretLine.Y); // (12 - 10) * lineHeight
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

        // ----- Match highlighting (every Find match in the viewport) -----

        [Fact]
        public async Task GetVisibleLines_WithHighlighter_ProducesARectPerMatch()
        {
            TextLayoutEngine e = await NewEngineAsync("cat dog cat\nxxxx", cw: 10, lh: 20);
            e.SetHighlighter(new LiteralLineHighlighter("cat", ignoreCase: false, wholeWord: false));

            VisibleLine line0 = e.GetVisibleLines(hasFocus: false, caretVisible: false)[0];

            Assert.Equal(2, line0.Matches.Count);
            Assert.Equal(TextLayoutEngine.TextPadding, line0.Matches[0].X);        // "cat" at char 0
            Assert.Equal(30.0, line0.Matches[0].Width);                            // 3 chars * 10
            Assert.Equal(80.0 + TextLayoutEngine.TextPadding, line0.Matches[1].X); // "cat" at char 8
            Assert.Equal(30.0, line0.Matches[1].Width);
        }

        [Fact]
        public async Task GetVisibleLines_NoHighlighter_HasNoMatchRects()
        {
            TextLayoutEngine e = await NewEngineAsync("cat dog cat", cw: 10, lh: 20);

            Assert.Empty(e.GetVisibleLines(hasFocus: false, caretVisible: false)[0].Matches);
        }

        [Fact]
        public async Task SetHighlighter_Null_ClearsMatchRects()
        {
            TextLayoutEngine e = await NewEngineAsync("cat dog cat", cw: 10, lh: 20);
            e.SetHighlighter(new LiteralLineHighlighter("cat", ignoreCase: false, wholeWord: false));
            Assert.NotEmpty(e.GetVisibleLines(hasFocus: false, caretVisible: false)[0].Matches);

            e.SetHighlighter(null);
            Assert.Empty(e.GetVisibleLines(hasFocus: false, caretVisible: false)[0].Matches);
        }

        [Fact]
        public async Task SetHighlighter_RequestsRedraw()
        {
            TextLayoutEngine e = await NewEngineAsync("cat dog cat", cw: 10, lh: 20);
            int redraws = 0;
            e.RedrawRequested += () => redraws++;

            e.SetHighlighter(new LiteralLineHighlighter("cat", ignoreCase: false, wholeWord: false));

            Assert.True(redraws >= 1);
        }

        [Fact]
        public async Task GetVisibleLines_MatchRects_FollowHorizontalScroll()
        {
            TextLayoutEngine e = await NewEngineAsync("cat dog cat", cw: 10, lh: 20, vw: 50, vh: 100);
            e.SetHighlighter(new LiteralLineHighlighter("cat", ignoreCase: false, wholeWord: false));
            e.GetVisibleLines(hasFocus: false, caretVisible: false); // compute extent before scrolling

            e.HorizontalOffset = 30;
            VisibleLine line0 = e.GetVisibleLines(hasFocus: false, caretVisible: false)[0];

            Assert.Equal(TextLayoutEngine.TextPadding - 30, line0.Matches[0].X); // first "cat" rides the scroll
        }

        // ----- Line-number gutter -----

        [Fact]
        public async Task Gutter_OffByDefault_OnComputesWidthFromDigits()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3), cw: 10, lh: 20);
            Assert.Equal(0, e.GutterWidthPx);

            e.ShowLineNumbers = true;
            Assert.Equal(2 * 10 + 12, e.GutterWidthPx); // 3 lines -> min 2 digits * 10px + 2*6 padding = 32
        }

        [Fact]
        public async Task Gutter_ShiftsTextAndSelectionRightByGutterWidth()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3), cw: 10, lh: 20);
            e.ShowLineNumbers = true;
            double gutter = e.GutterWidthPx;
            e.SelectMatch(0, 1, 3); // columns 1..4

            VisibleLine line = e.GetVisibleLines(hasFocus: true, caretVisible: true)[0];

            Assert.Equal(TextLayoutEngine.TextPadding + gutter, line.TextX);          // origin shifted by the gutter
            Assert.Equal(10.0 + TextLayoutEngine.TextPadding + gutter, line.SelectionX); // column 1 * 10 + origin
        }

        [Fact]
        public async Task Gutter_HitTest_AccountsForGutter()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3), cw: 10, lh: 20);
            e.ShowLineNumbers = true;
            double origin = TextLayoutEngine.TextPadding + e.GutterWidthPx;

            Assert.Equal(new TextPosition(0, 1), e.HitTest(origin + 12, 5)); // 1.2 chars -> column 1
            Assert.Equal(new TextPosition(0, 0), e.HitTest(2, 5));           // a click inside the gutter -> column 0
        }

        [Fact]
        public async Task Gutter_IsIncludedInHorizontalExtent()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDEFGHIJ", 5), cw: 10, lh: 20, vw: 50, vh: 100);
            e.GetVisibleLines(hasFocus: false, caretVisible: false);
            double withoutGutter = e.HorizontalExtentPx;

            e.ShowLineNumbers = true;
            e.GetVisibleLines(hasFocus: false, caretVisible: false);

            Assert.Equal(withoutGutter + e.GutterWidthPx, e.HorizontalExtentPx);
        }

        [Fact]
        public async Task ShowLineNumbers_TogglingRequestsRedraw()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3), cw: 10, lh: 20);
            int redraws = 0;
            e.RedrawRequested += () => redraws++;

            e.ShowLineNumbers = true;

            Assert.True(redraws >= 1);
        }

        // ----- Filter / grep view (filtered line source rendered by the engine) -----

        [Fact]
        public async Task FilteredLineSource_EngineRendersOnlyMatchingLines()
        {
            // A filtered source feeds the engine only the matching lines; the render model must show exactly
            // those (in order), proving the filter view reuses the normal rendering path.
            LineProvider provider = await TestData.BuildProviderAsync("apple\nBANANA\ncherry\nBANANA split\ndate");
            System.Collections.Generic.List<int> matches =
                LineFilter.Match(provider, l => l.Contains("BANANA", System.StringComparison.Ordinal), out _);
            var filtered = new FilteredLineSource(provider, matches);

            var e = new TextLayoutEngine { ViewportWidth = 400, ViewportHeight = 200 };
            e.SetMetrics(10, 20);
            e.SetProvider(filtered);

            Assert.Equal(2, e.TotalLines);
            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: false, caretVisible: false);
            Assert.Equal(2, lines.Count);
            Assert.Equal("BANANA", lines[0].Display);
            Assert.Equal("BANANA split", lines[1].Display);
        }

        // ----- Persistent highlight rules (colour matches by pattern) -----

        [Fact]
        public async Task HighlightRules_EngineProducesColoredRectsForMatchingLines()
        {
            HighlightColors.TryParse("red", out uint red);
            TextLayoutEngine e = await NewEngineAsync("ERROR here\nclean line", cw: 10, lh: 20, vw: 400, vh: 100);
            e.SetHighlightRules(HighlightRuleSet.Build(new[]
            {
                new HighlightRule { Pattern = "ERROR", Color = "red" },
            }));

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: false, caretVisible: false);

            RuleHighlightRect rect = Assert.Single(lines[0].RuleHighlights);
            Assert.Equal(red, rect.Argb);
            Assert.Equal(TextLayoutEngine.TextPadding, rect.X);       // char 0 -> x = padding
            Assert.Equal(50.0, rect.Width);                          // 5 chars * 10px
            Assert.Empty(lines[1].RuleHighlights);                   // no match on the second line
        }

        [Fact]
        public async Task HighlightRules_AreIndependentOfFindHighlighter()
        {
            TextLayoutEngine e = await NewEngineAsync("ERROR ERROR", cw: 10, lh: 20, vw: 400, vh: 100);
            e.SetHighlightRules(HighlightRuleSet.Build(new[]
            {
                new HighlightRule { Pattern = "ERROR", Color = "red" },
            }));

            // No Find highlighter set: Matches stays empty while RuleHighlights are populated.
            VisibleLine line = e.GetVisibleLines(hasFocus: false, caretVisible: false)[0];
            Assert.Empty(line.Matches);
            Assert.Equal(2, line.RuleHighlights.Count);

            // Clearing the rules removes the rule highlights.
            e.SetHighlightRules(null);
            Assert.Empty(e.GetVisibleLines(hasFocus: false, caretVisible: false)[0].RuleHighlights);
        }

        // ----- Bookmarks -----

        [Fact]
        public async Task ToggleBookmarkAtCaret_MarksTheCaretLineInTheRenderModel()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("LINE", 20), cw: 10, lh: 20, vw: 200, vh: 200);

            e.GoToLine(3);
            Assert.True(e.ToggleBookmarkAtCaret());   // now bookmarked
            Assert.True(e.HasBookmarks);

            e.GoToLine(0); // scroll to top so visible index == line index
            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: false, caretVisible: false);
            Assert.True(lines[3].IsBookmarked);
            Assert.False(lines[2].IsBookmarked);

            e.GoToLine(3);
            Assert.False(e.ToggleBookmarkAtCaret());  // toggled off
            Assert.False(e.HasBookmarks);
        }

        [Fact]
        public async Task BookmarkNavigation_MovesCaretWithWrapAround()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("LINE", 20), cw: 10, lh: 20, vw: 200, vh: 200);
            e.GoToLine(2); e.ToggleBookmarkAtCaret();
            e.GoToLine(8); e.ToggleBookmarkAtCaret();

            e.GoToLine(0);
            e.GoToNextBookmark();
            Assert.Equal(2, e.CaretPosition.Line);
            e.GoToNextBookmark();
            Assert.Equal(8, e.CaretPosition.Line);
            e.GoToNextBookmark();                     // past the last -> wrap to first
            Assert.Equal(2, e.CaretPosition.Line);
            e.GoToPreviousBookmark();                 // before the first -> wrap to last
            Assert.Equal(8, e.CaretPosition.Line);
        }

        [Fact]
        public async Task ClearBookmarks_RemovesAll()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("LINE", 20), cw: 10, lh: 20, vw: 200, vh: 200);
            e.GoToLine(3); e.ToggleBookmarkAtCaret();

            e.ClearBookmarks();

            Assert.False(e.HasBookmarks);
            e.GoToLine(0);
            Assert.False(e.GetVisibleLines(hasFocus: false, caretVisible: false)[3].IsBookmarked);
        }

        [Fact]
        public async Task SetProvider_ClearsBookmarks()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("LINE", 20), cw: 10, lh: 20, vw: 200, vh: 200);
            e.GoToLine(3); e.ToggleBookmarkAtCaret();
            Assert.True(e.HasBookmarks);

            LineProvider another = await TestData.BuildProviderAsync("a\nb\nc");
            e.SetProvider(another);

            Assert.False(e.HasBookmarks); // bookmarks are line indices into the previous file
        }

        // ----- Whitespace / control-character markers -----

        [Fact]
        public async Task ShowWhitespace_PopulatesMarkersOnlyWhenOn()
        {
            TextLayoutEngine e = await NewEngineAsync("a b\tc", cw: 10, lh: 20, vw: 400, vh: 100);

            // Off by default -> no markers.
            Assert.Empty(e.GetVisibleLines(hasFocus: false, caretVisible: false)[0].Whitespace);

            e.ShowWhitespace = true;
            IReadOnlyList<WhitespaceMarker> ws = e.GetVisibleLines(hasFocus: false, caretVisible: false)[0].Whitespace;

            // One space + one tab.
            Assert.Contains(ws, m => m.Kind == WhitespaceKind.Space);
            Assert.Contains(ws, m => m.Kind == WhitespaceKind.Tab);

            e.ShowWhitespace = false;
            Assert.Empty(e.GetVisibleLines(hasFocus: false, caretVisible: false)[0].Whitespace);
        }

        // ----- Selection statistics (status bar) -----

        [Fact]
        public async Task GetSelectionStats_NoSelection_ReturnsZero()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 5), cw: 10, lh: 20);

            SelectionStats stats = e.GetSelectionStats();

            Assert.Equal(0, stats.Characters);
            Assert.Equal(0, stats.Lines);
        }

        [Fact]
        public void GetSelectionStats_WithoutProvider_ReturnsZero()
        {
            var e = new TextLayoutEngine { ViewportWidth = 100, ViewportHeight = 100 };
            e.SetMetrics(10, 20);

            SelectionStats stats = e.GetSelectionStats();

            Assert.Equal(0, stats.Characters);
            Assert.Equal(0, stats.Lines);
        }

        [Fact]
        public async Task GetSelectionStats_SingleLine_CountsSelectedCharacters()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 5), cw: 10, lh: 20);
            e.SelectMatch(1, 1, 3); // select "BCD" on line 1

            SelectionStats stats = e.GetSelectionStats();

            Assert.Equal(3, stats.Characters);
            Assert.Equal(1, stats.Lines);
        }

        [Fact]
        public async Task GetSelectionStats_MultiLine_CountsCharactersIncludingNewlines()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3), cw: 10, lh: 20, vw: 200, vh: 200);
            e.PointerPress(25, 5, shift: false); // (0,2)
            e.PointerDrag(35, 45);               // (2,3)

            SelectionStats stats = e.GetSelectionStats();

            // line 0: chars 2..5 (3) + newline (1); line 1: all 5 + newline (1); line 2: chars 0..3 (3) = 13.
            Assert.Equal(13, stats.Characters);
            Assert.Equal(3, stats.Lines);
        }

        [Fact]
        public async Task GetSelectionStats_VeryLargeSelection_ReportsLinesButNotCharacters()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 6000), cw: 10, lh: 20);
            e.HandleKey(NavKey.SelectAll, shift: false);

            SelectionStats stats = e.GetSelectionStats();

            Assert.Equal(-1, stats.Characters); // too large to count cheaply
            Assert.Equal(6000, stats.Lines);
        }

        [Fact]
        public async Task GetSelectionTimestampDelta_NoSelection_ReturnsNull()
        {
            TextLayoutEngine e = await NewEngineAsync("2024-01-02 15:04:05 a\nmid\n2024-01-02 15:06:35 b");

            Assert.Null(e.GetSelectionTimestampDelta());
        }

        [Fact]
        public async Task GetSelectionTimestampDelta_SingleLineSelection_ReturnsNull()
        {
            TextLayoutEngine e = await NewEngineAsync("2024-01-02 15:04:05 a\n2024-01-02 15:06:35 b");
            e.SelectMatch(0, 0, 4); // select within line 0 only

            Assert.Null(e.GetSelectionTimestampDelta());
        }

        [Fact]
        public async Task GetSelectionTimestampDelta_MultiLineBothTimestamps_ReturnsDifference()
        {
            TextLayoutEngine e = await NewEngineAsync("2024-01-02 15:04:05 a\nmiddle\n2024-01-02 15:06:35 b");
            e.HandleKey(NavKey.SelectAll, shift: false);

            Assert.Equal(TimeSpan.FromSeconds(150), e.GetSelectionTimestampDelta());
        }

        [Fact]
        public async Task GetSelectionTimestampDelta_CommaMilliseconds_KeepsSubSecondPrecision()
        {
            // log4j / Python-logging timestamps (yyyy-MM-dd HH:mm:ss,SSS) in the same second: the delta must
            // honour the comma-milliseconds end to end rather than reporting 0.
            TextLayoutEngine e = await NewEngineAsync(
                "2026-06-17 22:05:51,119 a\nmiddle\n2026-06-17 22:05:51,500 b");
            e.HandleKey(NavKey.SelectAll, shift: false);

            Assert.Equal(TimeSpan.FromMilliseconds(381), e.GetSelectionTimestampDelta());
        }

        [Fact]
        public async Task GetSelectionTimestampDelta_EndpointNotTimestamp_ReturnsNull()
        {
            TextLayoutEngine e = await NewEngineAsync("2024-01-02 15:04:05 a\nmiddle\nno timestamp here");
            e.HandleKey(NavKey.SelectAll, shift: false);

            Assert.Null(e.GetSelectionTimestampDelta());
        }

        // ----- Copy with line numbers -----

        [Fact]
        public async Task BuildCopyTextWithLineNumbers_NoSelection_CopiesCaretLine()
        {
            TextLayoutEngine e = await NewEngineAsync("alpha\nbeta\ngamma", cw: 10, lh: 20);
            e.GoToLine(1); // caret on "beta" (line index 1 -> number 2), no selection

            Assert.Equal("2: beta", e.BuildCopyTextWithLineNumbers());
        }

        [Fact]
        public async Task BuildCopyTextWithLineNumbers_SelectAll_NumbersEveryLine()
        {
            TextLayoutEngine e = await NewEngineAsync("alpha\nbeta", cw: 10, lh: 20);
            e.HandleKey(NavKey.SelectAll, shift: false);

            Assert.Equal("1: alpha\r\n2: beta", e.BuildCopyTextWithLineNumbers());
        }

        // ----- Text stability: the render model must not shift the lines between redraws (the caret-blink
        //       jitter the user must never see again, #113 / #75). -----

        [Fact]
        public async Task GetVisibleLines_LineTopsAreExactMultiplesOfLineHeight()
        {
            // With a device-pixel-snapped line height (whole pixels) this guarantees every line top is also on
            // the pixel grid, so the text cannot land on a sub-pixel and shimmer.
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 100), lh: 20, vh: 200);

            IReadOnlyList<VisibleLine> lines = e.GetVisibleLines(hasFocus: true, caretVisible: true);

            for (int i = 0; i < lines.Count; i++)
            {
                Assert.Equal(i * 20.0, lines[i].Y);
            }
        }

        [Fact]
        public async Task GetVisibleLines_YPositionsAreStableAcrossCaretBlink()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 100), lh: 20, vh: 200);

            double[] caretOn = Ys(e.GetVisibleLines(hasFocus: true, caretVisible: true));
            double[] caretOff = Ys(e.GetVisibleLines(hasFocus: true, caretVisible: false));

            Assert.Equal(caretOn, caretOff); // the blink must not move any line
        }

        [Fact]
        public async Task GetVisibleLines_YPositionsAreStableAcrossFocus()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 100), lh: 20, vh: 200);

            double[] focused = Ys(e.GetVisibleLines(hasFocus: true, caretVisible: true));
            double[] unfocused = Ys(e.GetVisibleLines(hasFocus: false, caretVisible: false));

            Assert.Equal(focused, unfocused); // gaining/losing focus must not move any line
        }

        [Fact]
        public async Task GetVisibleLines_YPositionsAreStableAcrossRepeatedRedraws()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 100), lh: 20, vh: 200);

            double[] first = Ys(e.GetVisibleLines(hasFocus: true, caretVisible: true));
            double[] second = Ys(e.GetVisibleLines(hasFocus: true, caretVisible: true));

            Assert.Equal(first, second); // identical input → identical layout, every frame
        }

        private static double[] Ys(IReadOnlyList<VisibleLine> lines)
        {
            var ys = new double[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                ys[i] = lines[i].Y;
            }
            return ys;
        }
    }
}
