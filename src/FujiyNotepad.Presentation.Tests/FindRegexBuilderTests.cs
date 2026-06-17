using System.Text.RegularExpressions;

namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>Tests the Find bar's regex construction: whole-word wrapping and the match-case option.</summary>
    public class FindRegexBuilderTests
    {
        [Fact]
        public void Plain_MatchesSubstring_CaseSensitive()
        {
            Regex r = FindRegexBuilder.Build("cat", matchCase: true, wholeWord: false);

            Assert.Matches(r, "scatter");
            Assert.DoesNotMatch(r, "CAT");
        }

        [Fact]
        public void MatchCaseOff_IsCaseInsensitive()
        {
            Regex r = FindRegexBuilder.Build("cat", matchCase: false, wholeWord: false);

            Assert.Matches(r, "CAT");
            Assert.Matches(r, "Cat");
        }

        [Fact]
        public void WholeWord_MatchesStandaloneTokenOnly()
        {
            Regex r = FindRegexBuilder.Build("cat", matchCase: true, wholeWord: true);

            Assert.Matches(r, "a cat sat");
            Assert.DoesNotMatch(r, "scatter");
            Assert.DoesNotMatch(r, "category");
        }

        [Fact]
        public void WholeWord_WrapsAlternationSoItGroupsCorrectly()
        {
            // Without the (?:...) group, \bcat|dog\b would parse as (\bcat)|(dog\b).
            Regex r = FindRegexBuilder.Build("cat|dog", matchCase: true, wholeWord: true);

            Assert.Matches(r, "a dog ran");
            Assert.Matches(r, "a cat sat");
            Assert.DoesNotMatch(r, "category"); // 'cat' inside a word must not match
            Assert.DoesNotMatch(r, "dogma");    // 'dog' inside a word must not match
        }

        [Fact]
        public void InvalidPattern_Throws()
        {
            Assert.Throws<RegexParseException>(() => FindRegexBuilder.Build("(unclosed", matchCase: true, wholeWord: false));
        }
    }
}
