using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the catastrophic-backtracking guard on user-supplied regexes (issue #163): every construction path
    /// carries a per-match timeout, and the render path degrades to "rule off" instead of freezing.
    /// </summary>
    public class UserRegexTimeoutTests
    {
        // Classic exponential backtracker: every 'a' can be split between the inner and outer quantifier, so a
        // non-matching tail forces the engine through 2^n partitions.
        private const string CatastrophicPattern = @"(a+)+$";

        private static string EvilInput => new string('a', 40) + "b";

        [Fact]
        public void Create_CarriesTheMatchTimeout()
        {
            Regex r = UserRegex.Create("abc", RegexOptions.None);

            Assert.Equal(UserRegex.MatchTimeout, r.MatchTimeout);
            Assert.NotEqual(Regex.InfiniteMatchTimeout, r.MatchTimeout);
        }

        [Fact]
        public void FindRegexBuilder_CarriesTheMatchTimeout()
        {
            Assert.Equal(UserRegex.MatchTimeout, FindRegexBuilder.Build("cat", matchCase: true, wholeWord: false).MatchTimeout);
            Assert.Equal(UserRegex.MatchTimeout, FindRegexBuilder.Build("cat", matchCase: false, wholeWord: true).MatchTimeout);
        }

        [Fact]
        public void Create_StillThrowsForAMalformedPattern()
        {
            // The timeout must not change how an invalid pattern surfaces — callers show "Invalid regex".
            // RegexParseException derives from ArgumentException, which is what those catch clauses use.
            Assert.ThrowsAny<ArgumentException>(() => UserRegex.Create("(unclosed", RegexOptions.None));
        }

        [Fact]
        public void CatastrophicPattern_TimesOutInsteadOfHanging()
        {
            Regex r = UserRegex.Create(CatastrophicPattern, RegexOptions.None);
            var sw = Stopwatch.StartNew();

            Assert.Throws<RegexMatchTimeoutException>(() => r.IsMatch(EvilInput));

            sw.Stop();
            // Generous headroom over the budget: the point is that it is bounded at all, not that it is exact.
            Assert.True(sw.Elapsed < UserRegex.MatchTimeout + TimeSpan.FromSeconds(5),
                $"expected the match to be abandoned near the budget, took {sw.Elapsed}");
        }

        [Fact]
        public void RegexLineHighlighter_TimeoutDisablesTheRuleInsteadOfThrowing()
        {
            // Highlighting runs on the render path (TextCanvas.OnDraw has no try/catch), so a timeout must never
            // escape — and must not be retried per line per frame, which would freeze the UI just as badly.
            var highlighter = new RegexLineHighlighter(UserRegex.Create(CatastrophicPattern, RegexOptions.None));

            Assert.False(highlighter.TimedOut);
            Assert.Empty(highlighter.Find(EvilInput));
            Assert.True(highlighter.TimedOut);
        }

        [Fact]
        public void RegexLineHighlighter_OnceTimedOut_SubsequentLinesAreImmediate()
        {
            var highlighter = new RegexLineHighlighter(UserRegex.Create(CatastrophicPattern, RegexOptions.None));
            highlighter.Find(EvilInput); // trips the latch (pays the budget once)

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
            {
                Assert.Empty(highlighter.Find(EvilInput));
            }
            sw.Stop();

            // 200 lines is ~4 screens' worth. Un-latched this would cost 200 x the timeout.
            Assert.True(sw.Elapsed < UserRegex.MatchTimeout,
                $"a disabled rule must cost nothing, took {sw.Elapsed} for 200 lines");
        }

        [Fact]
        public void RegexLineHighlighter_NormalPattern_IsUnaffected()
        {
            var highlighter = new RegexLineHighlighter(UserRegex.Create("er+or", RegexOptions.IgnoreCase));

            IReadOnlyList<(int Start, int Length)> spans = highlighter.Find("an ERROR and an error");

            Assert.Equal(2, spans.Count);
            Assert.Equal((3, 5), spans[0]);
            Assert.Equal((16, 5), spans[1]);
            Assert.False(highlighter.TimedOut);
        }

        [Fact]
        public void HighlightRuleSet_CatastrophicRule_DoesNotThrowAndKeepsOtherRulesWorking()
        {
            // A bad rule is persisted in settings, so it would otherwise break every launch. The rest of the
            // set must keep highlighting.
            var set = HighlightRuleSet.Build(new[]
            {
                new HighlightRule { Pattern = CatastrophicPattern, IsRegex = true, MatchCase = true, Color = "Red" },
                new HighlightRule { Pattern = "aaa", IsRegex = false, MatchCase = true, Color = "Yellow" },
            });

            IReadOnlyList<HighlightSpan> spans = set.Find(EvilInput);

            Assert.NotEmpty(spans); // the literal rule still produced highlights
            Assert.All(spans, s => Assert.True(s.Length > 0));
        }
    }
}
