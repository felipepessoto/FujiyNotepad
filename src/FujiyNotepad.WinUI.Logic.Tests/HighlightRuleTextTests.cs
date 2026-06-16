namespace FujiyNotepad.WinUI.Logic.Tests
{
    /// <summary>Tests parsing/formatting of the editable highlight-rules text (color[/flags] pattern).</summary>
    public class HighlightRuleTextTests
    {
        [Fact]
        public void Parse_Literal_DefaultOptions()
        {
            List<HighlightRule> rules = HighlightRuleText.Parse("red ERROR");

            HighlightRule rule = Assert.Single(rules);
            Assert.Equal("ERROR", rule.Pattern);
            Assert.Equal("red", rule.Color);
            Assert.False(rule.IsRegex);
            Assert.False(rule.MatchCase);
        }

        [Fact]
        public void Parse_Flags_RegexAndCase()
        {
            List<HighlightRule> rules = HighlightRuleText.Parse(@"blue/regex,case Error\d+");

            HighlightRule rule = Assert.Single(rules);
            Assert.Equal(@"Error\d+", rule.Pattern);
            Assert.Equal("blue", rule.Color);
            Assert.True(rule.IsRegex);
            Assert.True(rule.MatchCase);
        }

        [Fact]
        public void Parse_RegexPattern_KeepsPipe()
        {
            // The pattern is the rest of the line, so regex alternation '|' survives intact.
            List<HighlightRule> rules = HighlightRuleText.Parse(@"green/regex \b(INFO|DEBUG)\b");

            Assert.Equal(@"\b(INFO|DEBUG)\b", Assert.Single(rules).Pattern);
        }

        [Fact]
        public void Parse_SkipsCommentsBlankLinesAndPatternlessLines()
        {
            string text = "# a comment\n\n   \nred ERROR\nblue\n";

            List<HighlightRule> rules = HighlightRuleText.Parse(text);

            HighlightRule rule = Assert.Single(rules);
            Assert.Equal("ERROR", rule.Pattern);
        }

        [Fact]
        public void Parse_HandlesCrLfLineEndings()
        {
            List<HighlightRule> rules = HighlightRuleText.Parse("red ERROR\r\norange WARN\r\n");

            Assert.Equal(2, rules.Count);
            Assert.Equal("ERROR", rules[0].Pattern);
            Assert.Equal("WARN", rules[1].Pattern);
        }

        [Fact]
        public void Parse_HandlesLoneCrLineEndings()
        {
            // WinUI's multiline TextBox.Text uses lone '\r' between lines; all rules must still parse.
            List<HighlightRule> rules = HighlightRuleText.Parse("red ERROR\rorange WARN\rgreen INFO");

            Assert.Equal(3, rules.Count);
            Assert.Equal("ERROR", rules[0].Pattern);
            Assert.Equal("WARN", rules[1].Pattern);
            Assert.Equal("INFO", rules[2].Pattern);
        }

        [Fact]
        public void Parse_MissingColor_LeavesDefault()
        {
            // A leading "/regex" (no colour before the slash) keeps the rule's default colour.
            List<HighlightRule> rules = HighlightRuleText.Parse("/regex ERROR");

            HighlightRule rule = Assert.Single(rules);
            Assert.Equal(HighlightColors.DefaultColorName, rule.Color);
            Assert.True(rule.IsRegex);
        }

        [Fact]
        public void Format_Then_Parse_RoundTrips()
        {
            var original = new List<HighlightRule>
            {
                new() { Pattern = "ERROR", Color = "red" },
                new() { Pattern = @"Warn\d+", Color = "#FFAA00", IsRegex = true, MatchCase = true },
            };

            List<HighlightRule> roundTripped = HighlightRuleText.Parse(HighlightRuleText.Format(original));

            Assert.Equal(original.Count, roundTripped.Count);
            for (int i = 0; i < original.Count; i++)
            {
                Assert.Equal(original[i].Pattern, roundTripped[i].Pattern);
                Assert.Equal(original[i].Color, roundTripped[i].Color);
                Assert.Equal(original[i].IsRegex, roundTripped[i].IsRegex);
                Assert.Equal(original[i].MatchCase, roundTripped[i].MatchCase);
            }
        }

        [Fact]
        public void Parse_HexColorFirst_IsRuleNotComment()
        {
            List<HighlightRule> rules = HighlightRuleText.Parse("#FFAA00 WARN\n# this is a real comment\n");

            HighlightRule rule = Assert.Single(rules);
            Assert.Equal("WARN", rule.Pattern);
            Assert.Equal("#FFAA00", rule.Color);
        }

        [Fact]
        public void Parse_EmptyOrNull_ReturnsEmpty()
        {
            Assert.Empty(HighlightRuleText.Parse(null));
            Assert.Empty(HighlightRuleText.Parse(""));
        }
    }
}
