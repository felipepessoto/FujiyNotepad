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

        [Fact]
        public void BuildLiteralWholeWord_UsesAsciiWordCharacters_MatchingTheByteScanner()
        {
            // .NET's \b would say U+6363 is a word character and reject this; the byte scanner that backs the
            // same feature uses ASCII-only word characters and accepts it. They must agree, because which of
            // the two runs is decided by whether indexing has finished — invisible to the user.
            Regex r = FindRegexBuilder.BuildLiteralWholeWord("ERROR", matchCase: true);

            Assert.Matches(r, "\u6363ERROR\u6363");
            Assert.DoesNotMatch(new Regex(@"\b(?:ERROR)\b"), "\u6363ERROR\u6363"); // what \b would have done
        }

        [Fact]
        public void BuildLiteralWholeWord_StillRejectsAsciiWordNeighbours()
        {
            Regex r = FindRegexBuilder.BuildLiteralWholeWord("cat", matchCase: true);

            Assert.Matches(r, "a cat sat");
            Assert.Matches(r, "cat");
            Assert.Matches(r, "(cat)");
            Assert.DoesNotMatch(r, "concat");
            Assert.DoesNotMatch(r, "cats");
            Assert.DoesNotMatch(r, "cat_9");
        }

        [Fact]
        public void BuildLiteralWholeWord_TreatsTheTermAsLiteral()
        {
            // The term is escaped, so regex metacharacters are matched as themselves.
            Regex r = FindRegexBuilder.BuildLiteralWholeWord("a.c", matchCase: true);

            Assert.Matches(r, "x a.c y");
            Assert.DoesNotMatch(r, "x abc y");
        }

        [Fact]
        public void BuildLiteralWholeWord_HonoursMatchCase()
        {
            Assert.DoesNotMatch(FindRegexBuilder.BuildLiteralWholeWord("cat", matchCase: true), "CAT");
            Assert.Matches(FindRegexBuilder.BuildLiteralWholeWord("cat", matchCase: false), "CAT");
        }

        [Fact]
        public void BuildLiteralWholeWord_CarriesTheMatchTimeout()
        {
            Assert.Equal(UserRegex.MatchTimeout, FindRegexBuilder.BuildLiteralWholeWord("cat", matchCase: true).MatchTimeout);
        }
    }
}
