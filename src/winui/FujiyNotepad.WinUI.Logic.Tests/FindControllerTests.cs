namespace FujiyNotepad.WinUI.Logic.Tests
{
    /// <summary>Tests the find-next state machine: term tracking, resume-past-match, and wrap/reset.</summary>
    public class FindControllerTests
    {
        [Fact]
        public void PrepareForwardSearch_NewTerm_StartsFromCaretAnchor()
        {
            var c = new FindController();

            Assert.Equal(100, c.PrepareForwardSearch("foo", caretAnchorOffset: 100));
            Assert.Equal("foo", c.Term);
            Assert.False(c.HasMatch);
        }

        [Fact]
        public void PrepareForwardSearch_SameTerm_AfterMatch_ResumesPastLastMatch()
        {
            var c = new FindController();
            c.PrepareForwardSearch("foo", 100);
            c.RecordMatch(250);

            Assert.True(c.HasMatch);
            Assert.Equal(251, c.PrepareForwardSearch("foo", caretAnchorOffset: 100));
        }

        [Fact]
        public void PrepareForwardSearch_SameTerm_NoMatchYet_StartsFromCaretAnchor()
        {
            var c = new FindController();
            c.PrepareForwardSearch("foo", 100);

            Assert.Equal(140, c.PrepareForwardSearch("foo", caretAnchorOffset: 140));
        }

        [Fact]
        public void PrepareForwardSearch_TermChange_RestartsFromCaretAnchor()
        {
            var c = new FindController();
            c.PrepareForwardSearch("foo", 100);
            c.RecordMatch(250);

            Assert.Equal(300, c.PrepareForwardSearch("bar", caretAnchorOffset: 300));
            Assert.Equal("bar", c.Term);
            Assert.False(c.HasMatch);
        }

        [Fact]
        public void RecordNoMatch_NextSearchWrapsToCaretAnchor()
        {
            var c = new FindController();
            c.PrepareForwardSearch("foo", 100);
            c.RecordMatch(250);
            c.RecordNoMatch();

            Assert.False(c.HasMatch);
            Assert.Equal(140, c.PrepareForwardSearch("foo", caretAnchorOffset: 140));
        }

        [Fact]
        public void Reset_ClearsTermAndMatch()
        {
            var c = new FindController();
            c.PrepareForwardSearch("foo", 100);
            c.RecordMatch(250);

            c.Reset();

            Assert.Null(c.Term);
            Assert.False(c.HasMatch);
            // After reset the same text is treated as a fresh term, so it starts from the caret again.
            Assert.Equal(140, c.PrepareForwardSearch("foo", caretAnchorOffset: 140));
        }

        [Fact]
        public void HasMatch_TogglesWithRecord()
        {
            var c = new FindController();
            c.PrepareForwardSearch("foo", 0);
            Assert.False(c.HasMatch);

            c.RecordMatch(5);
            Assert.True(c.HasMatch);

            c.RecordNoMatch();
            Assert.False(c.HasMatch);
        }
    }
}
