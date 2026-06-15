namespace FujiyNotepad.WinUI.Logic.Tests
{
    /// <summary>
    /// Tests the cross-cutting Find state machine that used to live inline in MainWindow: where each search
    /// starts (caret vs resume vs wrap), the caret-moved restart, the term/option-change reset, and the
    /// mutually-exclusive forward/backward wrap flags — exactly where the recent Find bug fixes were made.
    /// </summary>
    public class FindCoordinatorTests
    {
        private static TextPosition Pos(int line, int col) => new(line, col);

        // ----- Forward literal -----

        [Fact]
        public void Literal_FirstForwardSearch_StartsFromCaret()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));

            Assert.Equal(100, c.PlanLiteralForward("foo", caretOffset: 100));
        }

        [Fact]
        public void Literal_ForwardAfterMatch_ResumesPastTheMatch()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));
            c.PlanLiteralForward("foo", 100);
            c.RecordLiteralMatch(250, 3, Pos(5, 3));

            c.Begin("foo", Pos(5, 3)); // caret unchanged since the match
            Assert.Equal(253, c.PlanLiteralForward("foo", caretOffset: 100));
        }

        [Fact]
        public void Literal_ForwardNoMatch_ArmsWrapToStart_NextSearchStartsAtZero()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));
            c.PlanLiteralForward("foo", 100);
            c.RecordLiteralNoMatch(FindDirection.Forward, Pos(9, 9));

            Assert.True(c.ForwardWrapPending);
            Assert.False(c.BackwardWrapPending);

            c.Begin("foo", Pos(9, 9));
            Assert.Equal(0, c.PlanLiteralForward("foo", caretOffset: 100)); // wraps to document start
            Assert.False(c.ForwardWrapPending);                            // wrap consumed
        }

        // ----- Backward literal -----

        [Fact]
        public void Literal_FirstBackwardSearch_StopsBeforeSelectionStart()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));

            Assert.Equal(500, c.PlanLiteralBackward(selectionStartOffset: 500));
        }

        [Fact]
        public void Literal_BackwardNoMatch_ArmsWrapToEnd_NextSearchUsesMaxValue()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));
            c.PlanLiteralBackward(500);
            c.RecordLiteralNoMatch(FindDirection.Backward, Pos(0, 0));

            Assert.True(c.BackwardWrapPending);
            Assert.False(c.ForwardWrapPending);

            c.Begin("foo", Pos(0, 0));
            Assert.Equal(long.MaxValue, c.PlanLiteralBackward(500)); // wraps to document end
            Assert.False(c.BackwardWrapPending);
        }

        // ----- Caret-moved restart -----

        [Fact]
        public void CaretMovedSinceLastResult_RestartsFromCaret_NotResume()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));
            c.PlanLiteralForward("foo", 100);
            c.RecordLiteralMatch(250, 3, Pos(5, 3)); // last result left the caret at (5,3)

            c.Begin("foo", Pos(8, 0)); // the user clicked elsewhere
            Assert.Equal(400, c.PlanLiteralForward("foo", caretOffset: 400)); // from caret, not 253
        }

        [Fact]
        public void CaretMoved_ClearsPendingWrap()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));
            c.RecordLiteralNoMatch(FindDirection.Forward, Pos(5, 3));
            Assert.True(c.ForwardWrapPending);

            c.Begin("foo", Pos(8, 0)); // caret moved
            Assert.False(c.ForwardWrapPending);
        }

        // ----- Term/option (key) change -----

        [Fact]
        public void KeyChange_ClearsForwardWrap()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));
            c.RecordLiteralNoMatch(FindDirection.Forward, Pos(1, 1));
            Assert.True(c.ForwardWrapPending);

            c.Begin("bar", Pos(1, 1)); // changed term/options
            Assert.False(c.ForwardWrapPending);
        }

        [Fact]
        public void KeyChange_ClearsBackwardWrap()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));
            c.RecordLiteralNoMatch(FindDirection.Backward, Pos(1, 1));
            Assert.True(c.BackwardWrapPending);

            c.Begin("bar", Pos(1, 1));
            Assert.False(c.BackwardWrapPending);
        }

        // ----- Wrap flags are mutually exclusive -----

        [Fact]
        public void ForwardMiss_ThenBackwardMiss_LeavesOnlyBackwardArmed()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));

            c.RecordLiteralNoMatch(FindDirection.Forward, Pos(0, 0));
            Assert.True(c.ForwardWrapPending);

            c.RecordLiteralNoMatch(FindDirection.Backward, Pos(0, 0));
            Assert.True(c.BackwardWrapPending);
            Assert.False(c.ForwardWrapPending);
        }

        [Fact]
        public void Match_ClearsBothWrapFlags()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));
            c.RecordLiteralNoMatch(FindDirection.Forward, Pos(0, 0));
            Assert.True(c.ForwardWrapPending);

            c.RecordLiteralMatch(10, 3, Pos(1, 3));
            Assert.False(c.ForwardWrapPending);
            Assert.False(c.BackwardWrapPending);
        }

        // ----- Regex forward/backward -----

        [Fact]
        public void Regex_ForwardAfterMatch_ResumesPastTheMatch()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));
            Assert.Equal((2, 5), c.PlanRegexForward("foo", Pos(2, 5)));
            c.RecordRegexMatch(2, 5, 3, Pos(2, 8));

            c.Begin("foo", Pos(2, 8));
            Assert.Equal((2, 8), c.PlanRegexForward("foo", Pos(2, 8))); // resumes past col 5 + len 3
        }

        [Fact]
        public void Regex_ForwardWrap_StartsAtDocumentStart()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));
            c.RecordRegexNoMatch(FindDirection.Forward, Pos(9, 0));

            c.Begin("foo", Pos(9, 0));
            Assert.Equal((0, 0), c.PlanRegexForward("foo", Pos(9, 0)));
        }

        [Fact]
        public void Regex_BackwardFirst_StopsBeforeSelection_WrapGoesToLastLine()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));
            Assert.Equal((3, 7), c.PlanRegexBackward(Pos(3, 7), lineCount: 10));

            c.RecordRegexNoMatch(FindDirection.Backward, Pos(3, 7));
            c.Begin("foo", Pos(3, 7));
            Assert.Equal((9, int.MaxValue), c.PlanRegexBackward(Pos(3, 7), lineCount: 10));
        }

        // ----- Reset -----

        [Fact]
        public void Reset_ClearsWrapsResumeAndCaretAndKey()
        {
            var c = new FindCoordinator();
            c.Begin("foo", Pos(0, 0));
            c.PlanLiteralForward("foo", 100);
            c.RecordLiteralMatch(250, 3, Pos(5, 3));
            c.RecordLiteralNoMatch(FindDirection.Forward, Pos(9, 9)); // arm a wrap

            c.Reset();

            Assert.False(c.ForwardWrapPending);
            Assert.False(c.BackwardWrapPending);

            // Resume state is gone, so the next forward search starts from the supplied caret, not a resume.
            c.Begin("foo", Pos(5, 3));
            Assert.Equal(100, c.PlanLiteralForward("foo", caretOffset: 100));
        }
    }
}
