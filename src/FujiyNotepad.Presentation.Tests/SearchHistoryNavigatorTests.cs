namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the Up/Down recall navigator: walking older/newer, returning to the in-progress draft, capturing
    /// the draft on the first Up, restarting after a user edit, and the empty/boundary cases. The host loop is
    /// simulated by writing any non-null result back into <c>box</c> before the next call.
    /// </summary>
    public class SearchHistoryNavigatorTests
    {
        // History is newest-first, matching how the app stores it.
        private static SearchHistoryNavigator New(params string[] items) => new(items);

        [Fact]
        public void MoveUp_EmptyHistory_ReturnsNull()
        {
            var nav = New();

            Assert.Null(nav.MoveUp("draft"));
        }

        [Fact]
        public void MoveDown_OnDraftLine_ReturnsNull()
        {
            var nav = New("c", "b", "a");

            Assert.Null(nav.MoveDown("draft"));
        }

        [Fact]
        public void MoveUp_WalksFromNewestToOldest_ThenStops()
        {
            var nav = New("c", "b", "a"); // c newest

            Assert.Equal("c", nav.MoveUp(""));
            Assert.Equal("b", nav.MoveUp("c"));
            Assert.Equal("a", nav.MoveUp("b"));
            Assert.Null(nav.MoveUp("a")); // already at oldest, no change
        }

        [Fact]
        public void UpThenDown_ReturnsToTheCapturedDraft()
        {
            var nav = New("c", "b", "a");
            string box = "myDraft";

            box = nav.MoveUp(box)!;   // -> "c"
            Assert.Equal("c", box);
            box = nav.MoveUp(box)!;   // -> "b"
            Assert.Equal("b", box);

            box = nav.MoveDown(box)!; // -> "c"
            Assert.Equal("c", box);
            box = nav.MoveDown(box)!; // -> draft
            Assert.Equal("myDraft", box);

            Assert.Null(nav.MoveDown(box)); // already back on the draft line
        }

        [Fact]
        public void MoveUp_CapturesWhateverDraftWasInTheBox()
        {
            var nav = New("c", "b", "a");

            // The box already had a half-typed term; Up must remember it for the trip back.
            Assert.Equal("c", nav.MoveUp("half"));
            Assert.Equal("half", nav.MoveDown("c"));
        }

        [Fact]
        public void EditingARecalledEntry_ThenUp_RestartsFromTheNewDraft()
        {
            var nav = New("c", "b", "a");

            Assert.Equal("c", nav.MoveUp("")); // recall newest
            // User edits "c" -> "cc", then presses Up: the walk restarts and "cc" becomes the new draft.
            Assert.Equal("c", nav.MoveUp("cc"));
            Assert.Equal("cc", nav.MoveDown("c")); // back down lands on the edited draft
        }

        [Fact]
        public void EditingARecalledEntry_ThenDown_StopsAndKeepsTheEdit()
        {
            var nav = New("c", "b", "a");

            Assert.Equal("c", nav.MoveUp("")); // recall newest
            // User edits "c" -> "cc", then Down: navigation stops (null) leaving the edit in place.
            Assert.Null(nav.MoveDown("cc"));
            // A following Up treats "cc" as the draft and recalls the newest again.
            Assert.Equal("c", nav.MoveUp("cc"));
        }

        [Fact]
        public void SingleEntryHistory_UpThenDown_CyclesEntryAndDraft()
        {
            var nav = New("only");

            Assert.Equal("only", nav.MoveUp("d"));
            Assert.Null(nav.MoveUp("only"));   // nothing older
            Assert.Equal("d", nav.MoveDown("only")); // back to draft
        }
    }
}
