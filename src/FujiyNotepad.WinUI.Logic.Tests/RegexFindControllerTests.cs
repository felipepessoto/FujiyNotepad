namespace FujiyNotepad.WinUI.Logic.Tests
{
    /// <summary>Tests the regex find-next state machine in (line, char) space: resume-past-match and wrap/reset.</summary>
    public class RegexFindControllerTests
    {
        [Fact]
        public void PrepareForwardSearch_NewPattern_StartsFromCaret()
        {
            var c = new RegexFindController();

            Assert.Equal((10, 5), c.PrepareForwardSearch("fo+", caretLine: 10, caretChar: 5));
            Assert.False(c.HasMatch);
        }

        [Fact]
        public void PrepareForwardSearch_SamePattern_AfterMatch_ResumesPastMatchEnd()
        {
            var c = new RegexFindController();
            c.PrepareForwardSearch("fo+", 10, 5);
            c.RecordMatch(12, 7, 2);

            Assert.True(c.HasMatch);
            // Resume past the match end (column 7 + length 2 = 9), so matches never overlap.
            Assert.Equal((12, 9), c.PrepareForwardSearch("fo+", caretLine: 10, caretChar: 5));
        }

        [Fact]
        public void PrepareForwardSearch_SamePattern_NoMatchYet_StartsFromCaret()
        {
            var c = new RegexFindController();
            c.PrepareForwardSearch("fo+", 10, 5);

            Assert.Equal((3, 2), c.PrepareForwardSearch("fo+", caretLine: 3, caretChar: 2));
        }

        [Fact]
        public void PrepareForwardSearch_PatternChange_RestartsFromCaret()
        {
            var c = new RegexFindController();
            c.PrepareForwardSearch("fo+", 10, 5);
            c.RecordMatch(12, 7, 2);

            Assert.Equal((1, 1), c.PrepareForwardSearch("ba.", caretLine: 1, caretChar: 1));
            Assert.False(c.HasMatch);
        }

        [Fact]
        public void RecordNoMatch_NextSearchWrapsToCaret()
        {
            var c = new RegexFindController();
            c.PrepareForwardSearch("fo+", 10, 5);
            c.RecordMatch(12, 7, 2);
            c.RecordNoMatch();

            Assert.False(c.HasMatch);
            Assert.Equal((4, 0), c.PrepareForwardSearch("fo+", caretLine: 4, caretChar: 0));
        }

        [Fact]
        public void Reset_ClearsPatternAndMatch()
        {
            var c = new RegexFindController();
            c.PrepareForwardSearch("fo+", 10, 5);
            c.RecordMatch(12, 7, 2);

            c.Reset();

            Assert.False(c.HasMatch);
            Assert.Equal((4, 0), c.PrepareForwardSearch("fo+", caretLine: 4, caretChar: 0));
        }
    }
}
