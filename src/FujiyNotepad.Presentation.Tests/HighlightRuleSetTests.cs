namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>Tests compiling highlight rules into coloured per-line spans (the canvas paints these).</summary>
    public class HighlightRuleSetTests
    {
        private static uint Argb(string color)
        {
            HighlightColors.TryParse(color, out uint argb);
            return argb;
        }

        [Fact]
        public void Find_LiteralRule_ReturnsColoredSpans()
        {
            HighlightRuleSet set = HighlightRuleSet.Build(new[]
            {
                new HighlightRule { Pattern = "ERROR", Color = "red" },
            });

            IReadOnlyList<HighlightSpan> spans = set.Find("ERROR here ERROR");

            Assert.Equal(2, spans.Count);
            Assert.Equal(new HighlightSpan(0, 5, Argb("red")), spans[0]);
            Assert.Equal(new HighlightSpan(11, 5, Argb("red")), spans[1]);
        }

        [Fact]
        public void Find_MultipleRules_EachKeepsItsColor()
        {
            HighlightRuleSet set = HighlightRuleSet.Build(new[]
            {
                new HighlightRule { Pattern = "ERROR", Color = "red" },
                new HighlightRule { Pattern = "WARN", Color = "orange" },
            });

            IReadOnlyList<HighlightSpan> spans = set.Find("ERROR and WARN");

            Assert.Contains(new HighlightSpan(0, 5, Argb("red")), spans);
            Assert.Contains(new HighlightSpan(10, 4, Argb("orange")), spans);
        }

        [Fact]
        public void Find_LiteralIgnoresCaseByDefault()
        {
            HighlightRuleSet set = HighlightRuleSet.Build(new[]
            {
                new HighlightRule { Pattern = "error", Color = "red" },
            });

            Assert.Single(set.Find("ERROR"));
        }

        [Fact]
        public void Find_MatchCase_IsRespected()
        {
            HighlightRuleSet set = HighlightRuleSet.Build(new[]
            {
                new HighlightRule { Pattern = "error", Color = "red", MatchCase = true },
            });

            Assert.Empty(set.Find("ERROR"));
        }

        [Fact]
        public void Find_RegexRule_Matches()
        {
            HighlightRuleSet set = HighlightRuleSet.Build(new[]
            {
                new HighlightRule { Pattern = @"\d+", Color = "blue", IsRegex = true },
            });

            IReadOnlyList<HighlightSpan> spans = set.Find("code 404 and 500");

            Assert.Equal(2, spans.Count);
            Assert.Equal(new HighlightSpan(5, 3, Argb("blue")), spans[0]);
            Assert.Equal(new HighlightSpan(13, 3, Argb("blue")), spans[1]);
        }

        [Fact]
        public void Build_DropsInvalidRegexButKeepsValidRules()
        {
            HighlightRuleSet set = HighlightRuleSet.Build(new[]
            {
                new HighlightRule { Pattern = "ERROR", Color = "red" },
                new HighlightRule { Pattern = "(unclosed", Color = "blue", IsRegex = true },
            });

            Assert.Equal(1, set.Count);
        }

        [Fact]
        public void Build_SkipsEmptyPattern()
        {
            HighlightRuleSet set = HighlightRuleSet.Build(new[]
            {
                new HighlightRule { Pattern = "", Color = "red" },
            });

            Assert.Equal(0, set.Count);
        }

        [Fact]
        public void Build_UnknownColor_FallsBackToDefaultColor()
        {
            HighlightRuleSet set = HighlightRuleSet.Build(new[]
            {
                new HighlightRule { Pattern = "X", Color = "chartreuse-ish" },
            });

            HighlightSpan span = Assert.Single(set.Find("X"));
            Assert.Equal(Argb(HighlightColors.DefaultColorName), span.Argb);
        }

        [Fact]
        public void Find_NoMatch_ReturnsEmpty()
        {
            HighlightRuleSet set = HighlightRuleSet.Build(new[]
            {
                new HighlightRule { Pattern = "ZZZ", Color = "red" },
            });

            Assert.Empty(set.Find("nothing here"));
        }
    }
}
