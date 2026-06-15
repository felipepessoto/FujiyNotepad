using System.Text.RegularExpressions;

namespace FujiyNotepad.WinUI.Logic.Tests
{
    /// <summary>
    /// Tests the per-line Find highlighters used to paint every match in the viewport. The literal
    /// highlighter mirrors TextSearcher (ASCII fold, [A-Za-z0-9_] word boundary, non-overlapping); the regex
    /// highlighter mirrors RegexLineSearcher (non-empty matches).
    /// </summary>
    public class LineHighlighterTests
    {
        [Fact]
        public void Literal_FindsAllOccurrences()
        {
            var h = new LiteralLineHighlighter("cat", ignoreCase: false, wholeWord: false);
            Assert.Equal(new[] { (0, 3), (8, 3) }, h.Find("cat dog cat"));
        }

        [Fact]
        public void Literal_CaseSensitiveByDefault()
        {
            var h = new LiteralLineHighlighter("cat", ignoreCase: false, wholeWord: false);
            Assert.Equal(new[] { (0, 3) }, h.Find("cat Cat CAT"));
        }

        [Fact]
        public void Literal_IgnoreCase_FoldsAsciiOnly()
        {
            var h = new LiteralLineHighlighter("cat", ignoreCase: true, wholeWord: false);
            Assert.Equal(new[] { (0, 3), (4, 3), (8, 3) }, h.Find("cat Cat CAT"));
        }

        [Fact]
        public void Literal_WholeWord_ExcludesSubstrings()
        {
            var h = new LiteralLineHighlighter("cat", ignoreCase: false, wholeWord: true);
            // "cat" inside "catalog" (4) and "scatter" (16) is excluded; standalone ones at 0 and 12 are kept.
            Assert.Equal(new[] { (0, 3), (12, 3) }, h.Find("cat catalog cat scatter"));
        }

        [Fact]
        public void Literal_WholeWord_TreatsDigitsAndUnderscoreAsWordChars()
        {
            var h = new LiteralLineHighlighter("cat", ignoreCase: false, wholeWord: true);
            Assert.Equal(new[] { (13, 3) }, h.Find("cat_cat cat1 cat"));
        }

        [Fact]
        public void Literal_SelfOverlapping_IsNonOverlapping()
        {
            var h = new LiteralLineHighlighter("xx", ignoreCase: false, wholeWord: false);
            Assert.Equal(new[] { (0, 2), (2, 2) }, h.Find("xxxx"));
        }

        [Fact]
        public void Literal_NoMatch_IsEmpty()
        {
            var h = new LiteralLineHighlighter("zzz", ignoreCase: false, wholeWord: false);
            Assert.Empty(h.Find("cat dog"));
        }

        [Fact]
        public void Literal_EmptyTerm_IsEmpty()
        {
            var h = new LiteralLineHighlighter("", ignoreCase: false, wholeWord: false);
            Assert.Empty(h.Find("cat"));
        }

        [Fact]
        public void Literal_TermLongerThanLine_IsEmpty()
        {
            var h = new LiteralLineHighlighter("category", ignoreCase: false, wholeWord: false);
            Assert.Empty(h.Find("cat"));
        }

        [Fact]
        public void Regex_FindsNonEmptyMatches()
        {
            var h = new RegexLineHighlighter(new Regex(@"\d+"));
            Assert.Equal(new[] { (1, 2), (4, 3) }, h.Find("a12b345"));
        }

        [Fact]
        public void Regex_SkipsEmptyMatches()
        {
            var h = new RegexLineHighlighter(new Regex("a*"));
            Assert.Equal(new[] { (1, 1) }, h.Find("xax")); // empty matches at 0/2/3 are dropped
        }

        [Fact]
        public void Regex_HonoursIgnoreCaseOption()
        {
            var h = new RegexLineHighlighter(new Regex("cat", RegexOptions.IgnoreCase));
            Assert.Equal(new[] { (0, 3), (4, 3) }, h.Find("cat CAT"));
        }

        [Fact]
        public void Regex_NoMatch_IsEmpty()
        {
            var h = new RegexLineHighlighter(new Regex("zzz"));
            Assert.Empty(h.Find("cat dog"));
        }
    }
}
