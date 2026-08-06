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

        // Bound for the "a latched-off rule is free" checks. The gap being distinguished is enormous — a working
        // latch costs microseconds per line, a broken one costs the full 250 ms budget, so 200 lines would take
        // ~50 s. The bound is therefore deliberately loose: still fast enough to fail within seconds if the latch
        // regresses, but far outside anything CI jitter, scheduling or a GC pause could produce on its own.
        // Asserting near MatchTimeout itself would buy no detection power and only invite flakes.
        private static readonly TimeSpan LatchedLineBudget = TimeSpan.FromSeconds(5);

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
        public async Task CatastrophicPattern_TimesOutInsteadOfHanging()
        {
            Regex r = UserRegex.Create(CatastrophicPattern, RegexOptions.None);
            var sw = Stopwatch.StartNew();

            // Run the match on a worker and hard-bound the wait. If the timeout is ever lost, the match becomes
            // effectively infinite (2^40 partitions) and asserting on it directly would hang the whole test run
            // rather than failing it. The task returns the exception rather than assigning it across threads,
            // so the result is read from the awaited task and needs no reasoning about visibility.
            Task<Exception?> match = Task.Run<Exception?>(() =>
            {
                try
                {
                    r.IsMatch(EvilInput);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            });

            Task completed = await Task.WhenAny(match, Task.Delay(TimeSpan.FromSeconds(30)));
            sw.Stop();

            // If the budget were lost, the Delay would win this race instead of the match.
            Assert.True(ReferenceEquals(completed, match),
                "the match never returned within 30s - the per-match timeout is not being applied");
            Assert.IsType<RegexMatchTimeoutException>(await match);
            Assert.True(sw.Elapsed < UserRegex.MatchTimeout + TimeSpan.FromSeconds(5),
                $"expected the match to be abandoned near the budget, took {sw.Elapsed}");
        }

        [Fact]
        public void Create_StripsCompiled_SoTheAotInvariantCannotBeBrokenByACaller()
        {
            // RegexOptions.Compiled emits IL at runtime, which a Native-AOT build cannot do.
            Regex r = UserRegex.Create("abc", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            Assert.Equal(RegexOptions.IgnoreCase, r.Options); // Compiled dropped, everything else preserved
            Assert.Equal(UserRegex.MatchTimeout, r.MatchTimeout);
            Assert.Matches(r, "ABC");
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

                // Bail out as soon as the cost is clearly not "latched off". Without this, a regressed latch
                // would pay the budget on every iteration and take ~200 x MatchTimeout to report.
                Assert.True(sw.Elapsed < LatchedLineBudget,
                    $"a disabled rule must cost nothing, but {i + 1} lines already took {sw.Elapsed}");
            }
            sw.Stop();

            // 200 lines is ~4 screens' worth. Un-latched this would cost 200 x the timeout (~50 s).
            Assert.True(sw.Elapsed < LatchedLineBudget,
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
