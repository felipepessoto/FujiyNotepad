namespace FujiyNotepad.Presentation.Tests
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
            c.RecordMatch(250, 3);

            Assert.True(c.HasMatch);
            Assert.Equal(253, c.PrepareForwardSearch("foo", caretAnchorOffset: 100));
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
            c.RecordMatch(250, 3);

            Assert.Equal(300, c.PrepareForwardSearch("bar", caretAnchorOffset: 300));
            Assert.Equal("bar", c.Term);
            Assert.False(c.HasMatch);
        }

        [Fact]
        public void RecordNoMatch_NextSearchWrapsToCaretAnchor()
        {
            var c = new FindController();
            c.PrepareForwardSearch("foo", 100);
            c.RecordMatch(250, 3);
            c.RecordNoMatch();

            Assert.False(c.HasMatch);
            Assert.Equal(140, c.PrepareForwardSearch("foo", caretAnchorOffset: 140));
        }

        [Fact]
        public void Reset_ClearsTermAndMatch()
        {
            var c = new FindController();
            c.PrepareForwardSearch("foo", 100);
            c.RecordMatch(250, 3);

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

            c.RecordMatch(5, 3);
            Assert.True(c.HasMatch);

            c.RecordNoMatch();
            Assert.False(c.HasMatch);
        }

        [Fact]
        public void PrepareForwardSearch_ResumesPastMatchEnd_NonOverlapping()
        {
            var c = new FindController();
            c.PrepareForwardSearch("xxx", 0);
            c.RecordMatch(0, 3);

            // Resume past the match end (offset 0 + length 3 = 3), not one byte past the start, so searching
            // "xxx" in "xxxxxx" finds 0 then 3 - not 0/1/2/3.
            Assert.Equal(3, c.PrepareForwardSearch("xxx", caretAnchorOffset: 0));
        }
    }
}
