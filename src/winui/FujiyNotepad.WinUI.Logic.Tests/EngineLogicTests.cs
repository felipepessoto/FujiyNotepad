using FujiyNotepad.Core;

namespace FujiyNotepad.WinUI.Logic.Tests
{
    /// <summary>
    /// Deterministic unit tests for <see cref="TextLayoutEngine"/>'s scroll, hit-testing, caret navigation,
    /// selection and copy logic. Metrics are injected as a fixed monospace cell (10x20) so positions are
    /// exact and no graphics device is required (these run on a normal .NET test host).
    /// </summary>
    public class EngineLogicTests
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

        private static void Press(TextLayoutEngine e, NavKey k) => e.HandleKey(k, shift: false);
        private static void PressShift(TextLayoutEngine e, NavKey k) => e.HandleKey(k, shift: true);

        // ----- Provider / viewport -----

        [Fact]
        public async Task SetProvider_StartsAtTopLeftWithCaretAtOrigin()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 5));

            Assert.Equal(5, e.TotalLines);
            Assert.Equal(0, e.FirstVisibleLine);
            Assert.Equal(0.0, e.HorizontalOffset);
            Assert.Equal(new TextPosition(0, 0), e.CaretPosition);
        }

        [Fact]
        public async Task ViewportMetrics_ComputeVisibleLinesAndMaxScroll()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.ManyLines(100), lh: 20, vh: 200);

            Assert.Equal(10, e.FullyVisibleLineCount);
            Assert.Equal(90, e.MaxFirstLine);
        }

        // ----- Scrolling -----

        [Fact]
        public async Task SetFirstVisibleLine_ClampsToRange()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.ManyLines(100));

            e.FirstVisibleLine = 500;
            Assert.Equal(90, e.FirstVisibleLine);

            e.FirstVisibleLine = -5;
            Assert.Equal(0, e.FirstVisibleLine);
        }

        [Fact]
        public async Task SetFirstVisibleLine_RaisesViewChangedOnlyWhenValueChanges()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.ManyLines(100));
            int raised = 0;
            e.ViewChanged += () => raised++;

            e.FirstVisibleLine = 10;
            e.FirstVisibleLine = 10;

            Assert.Equal(1, raised);
        }

        [Fact]
        public async Task ScrollByWheelDelta_ScrollsThreeLinesPerNotch()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.ManyLines(100));

            e.ScrollByWheelDelta(-120);
            Assert.Equal(3, e.FirstVisibleLine);

            e.ScrollByWheelDelta(-240);
            Assert.Equal(9, e.FirstVisibleLine);

            e.ScrollByWheelDelta(120);
            Assert.Equal(6, e.FirstVisibleLine);
        }

        // ----- Go to line -----

        [Fact]
        public async Task GoToLine_MovesCaretAndScrolls()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.ManyLines(100));

            e.GoToLine(50);
            Assert.Equal(50, e.FirstVisibleLine);
            Assert.Equal(new TextPosition(50, 0), e.CaretPosition);

            e.GoToLine(99);
            Assert.Equal(90, e.FirstVisibleLine);
            Assert.Equal(new TextPosition(99, 0), e.CaretPosition);
        }

        [Fact]
        public async Task GoToLine_ClampsOutOfRangeTargets()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.ManyLines(100));

            e.GoToLine(10_000);
            Assert.Equal(new TextPosition(99, 0), e.CaretPosition);

            e.GoToLine(-7);
            Assert.Equal(new TextPosition(0, 0), e.CaretPosition);
        }

        [Fact]
        public void GoToLine_WithoutProvider_IsNoOp()
        {
            var e = new TextLayoutEngine { ViewportWidth = 100, ViewportHeight = 200 };
            e.SetMetrics(10, 20);

            e.GoToLine(5);

            Assert.Equal(new TextPosition(0, 0), e.CaretPosition);
            Assert.Equal(0, e.FirstVisibleLine);
        }

        // ----- Total-line updates (background indexing) -----

        [Fact]
        public async Task UpdateTotalLines_ShrinkClampsFirstVisibleLine()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.ManyLines(100));
            e.FirstVisibleLine = 90;

            e.UpdateTotalLines(20);

            Assert.Equal(20, e.TotalLines);
            Assert.Equal(10, e.FirstVisibleLine);
        }

        [Fact]
        public async Task UpdateTotalLines_SameCount_DoesNotRaiseViewChanged()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.ManyLines(100));
            int raised = 0;
            e.ViewChanged += () => raised++;

            e.UpdateTotalLines(100);

            Assert.Equal(0, raised);
        }

        // ----- Hit-testing -----

        [Fact]
        public async Task HitTest_MapsPointToLineAndNearestColumn()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDEFGHIJ", 5), cw: 10, lh: 20);

            Assert.Equal(new TextPosition(1, 3), e.HitTest(35, 25));   // x 3.5 rounds down
            Assert.Equal(new TextPosition(1, 4), e.HitTest(36, 25));   // x 3.6 rounds up
            Assert.Equal(new TextPosition(0, 0), e.HitTest(-5, -5));   // clamps to origin
            Assert.Equal(new TextPosition(0, 10), e.HitTest(1000, 5)); // past end of line
        }

        [Fact]
        public async Task HitTest_RespectsScrollOffset()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDEFGHIJ", 50), cw: 10, lh: 20);
            e.FirstVisibleLine = 5;

            Assert.Equal(new TextPosition(5, 0), e.HitTest(0, 0));
            Assert.Equal(new TextPosition(7, 0), e.HitTest(0, 40));
        }

        // ----- Pointer selection -----

        [Fact]
        public async Task PointerPress_NoShift_SetsCaretAndAnchor()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDEFGHIJ", 5), cw: 10, lh: 20);

            e.PointerPress(35, 25, shift: false);

            Assert.Equal(new TextPosition(1, 3), e.CaretPosition);
            Assert.Equal(new TextPosition(1, 3), e.Anchor);
        }

        [Fact]
        public async Task PointerPress_WithShift_KeepsAnchorAndExtends()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDEFGHIJ", 5), cw: 10, lh: 20);

            e.PointerPress(35, 25, shift: false); // anchor + caret at (1,3)
            e.PointerPress(5, 5, shift: true);    // caret jumps, anchor stays

            Assert.Equal(new TextPosition(0, 0), e.CaretPosition);
            Assert.Equal(new TextPosition(1, 3), e.Anchor);

            (TextPosition start, TextPosition end) = e.NormalizedSelection();
            Assert.Equal(new TextPosition(0, 0), start);
            Assert.Equal(new TextPosition(1, 3), end);
        }

        [Fact]
        public async Task PointerDrag_ExtendsSelectionFromAnchor()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDEFGHIJ", 10), cw: 10, lh: 20);

            e.PointerPress(5, 5, shift: false); // (0,0)
            e.PointerDrag(35, 65);              // line 3, col 3

            Assert.Equal(new TextPosition(3, 3), e.CaretPosition);
            Assert.Equal(new TextPosition(0, 0), e.Anchor);
        }

        [Fact]
        public async Task PointerDrag_BelowViewport_AutoScrollsDown()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDEFGHIJ", 50), cw: 10, lh: 20, vh: 200);

            e.PointerPress(5, 5, shift: false);
            e.PointerDrag(5, 250); // past the bottom edge

            Assert.Equal(1, e.FirstVisibleLine);
        }

        [Fact]
        public async Task PointerDrag_AboveViewport_AutoScrollsUp()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDEFGHIJ", 50), cw: 10, lh: 20);
            e.FirstVisibleLine = 5;

            e.PointerPress(5, 5, shift: false);
            e.PointerDrag(5, -5); // past the top edge

            Assert.Equal(4, e.FirstVisibleLine);
        }

        // ----- Keyboard navigation -----

        [Fact]
        public async Task Key_RightAndLeft_MoveAndWrapAcrossLines()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("AB", 3), cw: 10, lh: 20);
            e.GoToLine(0);

            Press(e, NavKey.Right);
            Assert.Equal(new TextPosition(0, 1), e.CaretPosition);
            Press(e, NavKey.Right);
            Assert.Equal(new TextPosition(0, 2), e.CaretPosition); // end of line 0
            Press(e, NavKey.Right);
            Assert.Equal(new TextPosition(1, 0), e.CaretPosition); // wraps to next line
            Press(e, NavKey.Left);
            Assert.Equal(new TextPosition(0, 2), e.CaretPosition); // wraps back
            Press(e, NavKey.Left);
            Assert.Equal(new TextPosition(0, 1), e.CaretPosition);
        }

        [Fact]
        public async Task Key_Left_AtDocumentStart_Stays()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("AB", 3));
            e.GoToLine(0);

            Press(e, NavKey.Left);

            Assert.Equal(new TextPosition(0, 0), e.CaretPosition);
        }

        [Fact]
        public async Task Key_Right_AtDocumentEnd_Stays()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("AB", 3));
            Press(e, NavKey.DocumentEnd); // (2,2)

            Press(e, NavKey.Right);

            Assert.Equal(new TextPosition(2, 2), e.CaretPosition);
        }

        [Fact]
        public async Task Key_DownAndUp_PreserveDesiredColumn()
        {
            TextLayoutEngine e = await NewEngineAsync("ABCDEFGH\nAB\nABCDEFGH\n");
            e.GoToLine(0);
            for (int i = 0; i < 5; i++)
            {
                Press(e, NavKey.Right); // (0,5)
            }

            Press(e, NavKey.Down);
            Assert.Equal(new TextPosition(1, 2), e.CaretPosition); // clamped to short line
            Press(e, NavKey.Down);
            Assert.Equal(new TextPosition(2, 5), e.CaretPosition); // desired column restored
            Press(e, NavKey.Up);
            Assert.Equal(new TextPosition(1, 2), e.CaretPosition);
            Press(e, NavKey.Up);
            Assert.Equal(new TextPosition(0, 5), e.CaretPosition);
        }

        [Fact]
        public async Task Key_LineStartAndLineEnd_MoveWithinLine()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3));
            e.GoToLine(1);

            Press(e, NavKey.LineEnd);
            Assert.Equal(new TextPosition(1, 5), e.CaretPosition);
            Press(e, NavKey.LineStart);
            Assert.Equal(new TextPosition(1, 0), e.CaretPosition);
        }

        [Fact]
        public async Task Key_DocumentStartAndEnd_MoveToBounds()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3));
            e.GoToLine(1);

            Press(e, NavKey.DocumentEnd);
            Assert.Equal(new TextPosition(2, 5), e.CaretPosition);
            Press(e, NavKey.DocumentStart);
            Assert.Equal(new TextPosition(0, 0), e.CaretPosition);
        }

        [Fact]
        public async Task Key_ShiftRight_ExtendsSelection()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3));
            e.GoToLine(0);

            PressShift(e, NavKey.Right);

            Assert.Equal(new TextPosition(0, 1), e.CaretPosition);
            Assert.Equal(new TextPosition(0, 0), e.Anchor);
        }

        [Fact]
        public async Task Key_SelectAll_WithoutRaisingCaretChanged()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3));
            bool caretRaised = false;
            e.CaretChanged += _ => caretRaised = true;

            KeyResult result = e.HandleKey(NavKey.SelectAll, shift: false);

            Assert.True(result.Handled);
            Assert.Equal(new TextPosition(0, 0), e.Anchor);
            Assert.Equal(new TextPosition(2, 5), e.CaretPosition);
            Assert.False(caretRaised); // matches the original SelectAll (no status-bar update)
        }

        [Fact]
        public async Task Key_Copy_ReturnsSelectedText()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3));
            e.SelectMatch(1, 1, 3); // selects "BCD" on line 1

            KeyResult result = e.HandleKey(NavKey.Copy, shift: false);

            Assert.True(result.Handled);
            Assert.Equal("BCD", result.CopyText);
        }

        [Fact]
        public async Task Key_Copy_NoSelection_IsHandledButCopiesNothing()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3));
            e.GoToLine(0);

            KeyResult result = e.HandleKey(NavKey.Copy, shift: false);

            Assert.True(result.Handled);
            Assert.Null(result.CopyText);
        }

        [Fact]
        public void Key_WithoutProvider_IsNotHandled()
        {
            var e = new TextLayoutEngine { ViewportWidth = 100, ViewportHeight = 200 };
            e.SetMetrics(10, 20);

            KeyResult result = e.HandleKey(NavKey.Right, shift: false);

            Assert.False(result.Handled);
        }

        // ----- Copy / selection text -----

        [Fact]
        public async Task BuildCopyText_SingleLineSelection()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3));
            e.SelectMatch(0, 1, 3);

            Assert.Equal("BCD", e.BuildCopyText());
        }

        [Fact]
        public async Task BuildCopyText_MultiLineSelection_JoinsWithCrlf()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3), cw: 10, lh: 20, vw: 200, vh: 200);

            e.PointerPress(25, 5, shift: false); // (0,2)
            e.PointerDrag(35, 45);               // (2,3)

            Assert.Equal("CDE\r\nABCDE\r\nABC", e.BuildCopyText());
        }

        [Fact]
        public async Task BuildCopyText_NoSelection_ReturnsNull()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3));
            e.GoToLine(0);

            Assert.Null(e.BuildCopyText());
        }

        [Fact]
        public async Task NormalizedSelection_OrdersEndpoints()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3), cw: 10, lh: 20, vh: 200);

            e.PointerPress(35, 5, shift: false); // upper anchor
            e.PointerPress(5, 45, shift: true);  // lower-left caret

            (TextPosition start, TextPosition end) = e.NormalizedSelection();
            Assert.True(start <= end);
        }

        [Fact]
        public async Task SelectMatch_ClampsColumnsToLine()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.RepeatLines("ABCDE", 3));

            e.SelectMatch(0, 3, 100);

            Assert.Equal(new TextPosition(0, 3), e.Anchor);
            Assert.Equal(new TextPosition(0, 5), e.CaretPosition);
        }

        [Fact]
        public async Task SelectMatch_ScrollsTargetLineIntoView()
        {
            TextLayoutEngine e = await NewEngineAsync(TestData.ManyLines(100));

            e.SelectMatch(50, 0, 2);

            Assert.Equal(50, e.FirstVisibleLine);
        }

        // ----- Double-click word selection -----

        [Theory]
        [InlineData("foo bar baz", 1, 0, 3)]   // inside "foo"
        [InlineData("foo bar baz", 5, 4, 7)]   // inside "bar"
        [InlineData("foo_bar baz", 2, 0, 7)]   // '_' is a word char -> "foo_bar"
        [InlineData("foo  bar", 4, 3, 5)]      // the run of spaces
        [InlineData("a, b", 1, 1, 2)]          // ',' is its own "other" class
        [InlineData("", 0, 0, 0)]              // empty line
        public void WordBoundaries_SelectsRunOfSameClass(string line, int index, int start, int end)
        {
            Assert.Equal((start, end), TextLayoutEngine.WordBoundaries(line, index));
        }

        [Fact]
        public async Task SelectWordAt_SelectsWordUnderPoint()
        {
            TextLayoutEngine e = await NewEngineAsync("foo bar baz\n", cw: 10, lh: 20);

            e.SelectWordAt(15, 5); // x 1.5 -> char 1 ('o' in "foo")

            Assert.Equal(new TextPosition(0, 0), e.Anchor);
            Assert.Equal(new TextPosition(0, 3), e.CaretPosition);
        }

        // ----- Configurable tab size -----

        [Fact]
        public async Task TabSize_ChangesTabExpansion()
        {
            TextLayoutEngine e = await NewEngineAsync("\tX\n", cw: 10, lh: 20, vw: 200);

            Assert.Equal("    X", e.GetVisibleLines(hasFocus: false, caretVisible: false)[0].Display); // default 4

            e.TabSize = 2;
            Assert.Equal("  X", e.GetVisibleLines(hasFocus: false, caretVisible: false)[0].Display);
        }

        [Fact]
        public async Task TabSize_ClampsToMinimumOne()
        {
            TextLayoutEngine e = await NewEngineAsync("\tX\n");

            e.TabSize = 0;

            Assert.Equal(1, e.TabSize);
        }
    }
}
