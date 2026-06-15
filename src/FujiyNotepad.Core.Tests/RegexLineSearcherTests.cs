using System.Text.RegularExpressions;
using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests
{
    public class RegexLineSearcherTests
    {
        private sealed class FakeLines : ILineSource
        {
            private readonly string[] lines;
            public FakeLines(params string[] lines) => this.lines = lines;
            public int LineCount => lines.Length;
            public string GetLine(int lineIndex) => lines[lineIndex];
        }

        private static RegexLineSearcher Searcher(params string[] lines) => new(new FakeLines(lines));

        [Fact]
        public void FindNext_ReturnsFirstMatch()
        {
            var s = Searcher("abc abc");
            Assert.Equal(new RegexLineSearcher.LineMatch(0, 0, 3), s.FindNext(new Regex("abc"), 0, 0));
        }

        [Fact]
        public void FindNext_RespectsStartChar()
        {
            var s = Searcher("abc abc");
            Assert.Equal(new RegexLineSearcher.LineMatch(0, 4, 3), s.FindNext(new Regex("abc"), 0, 1));
        }

        [Fact]
        public void FindNext_ScansForwardAcrossLines()
        {
            var s = Searcher("foo", "bar baz");
            Assert.Equal(new RegexLineSearcher.LineMatch(1, 0, 2), s.FindNext(new Regex("ba"), 0, 0));
        }

        [Fact]
        public void FindNext_NoMatch_ReturnsNull()
        {
            var s = Searcher("abc", "def");
            Assert.Null(s.FindNext(new Regex("xyz"), 0, 0));
        }

        [Fact]
        public void FindNext_SkipsZeroLengthMatches()
        {
            // "a*" matches empty at 0 and 1; the first real (non-empty) match is the 'a' at index 2.
            var s = Searcher("xxabc");
            Assert.Equal(new RegexLineSearcher.LineMatch(0, 2, 1), s.FindNext(new Regex("a*"), 0, 0));
        }

        [Fact]
        public void FindNext_HonoursRegexOptions_IgnoreCase()
        {
            var s = Searcher("ABC");
            Assert.Equal(new RegexLineSearcher.LineMatch(0, 0, 3), s.FindNext(new Regex("abc", RegexOptions.IgnoreCase), 0, 0));
        }

        [Fact]
        public void FindNext_WholeWordAnchors_ExcludeSubstringMatches()
        {
            var s = Searcher("cat catalog");
            var regex = new Regex(@"\bcat\b");
            Assert.Equal(new RegexLineSearcher.LineMatch(0, 0, 3), s.FindNext(regex, 0, 0));
            Assert.Null(s.FindNext(regex, 0, 1)); // "cat" in "catalog" is not a whole word
        }

        [Fact]
        public void CountAll_CountsNonEmptyMatchesAcrossLines()
        {
            var s = Searcher("a a", "aa", "b");
            Assert.Equal(4, s.CountAll(new Regex("a")));
        }

        [Fact]
        public void CountAll_SkipsZeroLengthMatches()
        {
            var s = Searcher("abc");
            Assert.Equal(0, s.CountAll(new Regex("x*")));
        }

        [Fact]
        public void CountAll_PreCancelled_ReturnsZero()
        {
            var s = Searcher("aaa", "aaa");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Equal(0, s.CountAll(new Regex("a"), null, cts.Token));
        }

        [Fact]
        public void FindPrevious_ReturnsLastMatchOnLine()
        {
            var s = Searcher("abc abc abc");
            // From the end of the line, the previous match is the last "abc".
            Assert.Equal(new RegexLineSearcher.LineMatch(0, 8, 3), s.FindPrevious(new Regex("abc"), 0, 11));
        }

        [Fact]
        public void FindPrevious_RespectsStartChar_OnlyMatchesStartingBeforeIt()
        {
            var s = Searcher("abc abc abc");
            // startChar 8 excludes the match starting at 8; the previous one starts at 4.
            Assert.Equal(new RegexLineSearcher.LineMatch(0, 4, 3), s.FindPrevious(new Regex("abc"), 0, 8));
        }

        [Fact]
        public void FindPrevious_ScansBackwardAcrossLines()
        {
            var s = Searcher("bar baz", "foo");
            // No match on line 1; the previous match is the last "ba" on line 0.
            Assert.Equal(new RegexLineSearcher.LineMatch(0, 4, 2), s.FindPrevious(new Regex("ba"), 1, 0));
        }

        [Fact]
        public void FindPrevious_NoMatchBefore_ReturnsNull()
        {
            var s = Searcher("abc");
            Assert.Null(s.FindPrevious(new Regex("abc"), 0, 0));
        }

        [Fact]
        public void FindPrevious_SkipsZeroLengthMatches()
        {
            // "a*" yields empty matches plus the real 'a' at 2; the previous non-empty match is that 'a'.
            var s = Searcher("xxaxx");
            Assert.Equal(new RegexLineSearcher.LineMatch(0, 2, 1), s.FindPrevious(new Regex("a*"), 0, 5));
        }

        [Fact]
        public void FindPrevious_HonoursRegexOptions_IgnoreCase()
        {
            var s = Searcher("abc ABC");
            Assert.Equal(new RegexLineSearcher.LineMatch(0, 4, 3), s.FindPrevious(new Regex("abc", RegexOptions.IgnoreCase), 0, 7));
        }

        [Fact]
        public void FindPrevious_WholeWordAnchors_ExcludeSubstringMatches()
        {
            var s = Searcher("cat catalog cat");
            var regex = new Regex(@"\bcat\b");
            // The "cat" inside "catalog" is skipped; the previous whole word before col 15 is at 12.
            Assert.Equal(new RegexLineSearcher.LineMatch(0, 12, 3), s.FindPrevious(regex, 0, 15));
        }
    }
}
