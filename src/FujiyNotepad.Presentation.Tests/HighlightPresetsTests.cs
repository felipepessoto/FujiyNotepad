namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the built-in highlight presets (issue #104): every preset parses to valid, compilable rules with
    /// recognized colours, and matches a representative sample line. Fully headless over the existing parser
    /// and rule set.
    /// </summary>
    public class HighlightPresetsTests
    {
        [Fact]
        public void All_AreNamedAndNonEmpty_WithUniqueNames()
        {
            Assert.NotEmpty(HighlightPresets.All);
            foreach (HighlightPreset p in HighlightPresets.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Name));
                Assert.False(string.IsNullOrWhiteSpace(p.RulesText));
            }

            int distinct = HighlightPresets.All.Select(p => p.Name).Distinct().Count();
            Assert.Equal(HighlightPresets.All.Count, distinct);
        }

        [Fact]
        public void EveryPreset_ParsesToRules_ThatAllCompile_WithRecognizedColours()
        {
            foreach (HighlightPreset p in HighlightPresets.All)
            {
                List<HighlightRule> rules = HighlightRuleText.Parse(p.RulesText);
                Assert.NotEmpty(rules);

                // Every colour token resolves on its own (no silent fall-back to the default colour).
                foreach (HighlightRule r in rules)
                {
                    Assert.True(HighlightColors.TryParse(r.Color, out _), $"{p.Name}: unrecognized colour '{r.Color}'");
                }

                // Every rule compiles: an invalid regex would be dropped, so the set must keep them all.
                HighlightRuleSet set = HighlightRuleSet.Build(rules);
                Assert.Equal(rules.Count, set.Count);
            }
        }

        [Theory]
        [InlineData("Log levels", "2026-06-17 22:05:51 ERROR connection refused")]
        [InlineData("Web access log", "127.0.0.1 - - [17/Jun/2026] \"GET /index.html HTTP/1.1\" 200 1234")]
        [InlineData("JSON", "{\"status\": \"ok\", \"count\": 42, \"ready\": true}")]
        [InlineData("Timestamps & IDs", "2026-06-17 22:05:51 host 10.0.0.5 id 3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
        public void Preset_MatchesRepresentativeLine(string name, string sample)
        {
            HighlightPreset preset = HighlightPresets.All.Single(p => p.Name == name);
            HighlightRuleSet set = HighlightRuleSet.Build(HighlightRuleText.Parse(preset.RulesText));

            Assert.NotEmpty(set.Find(sample));
        }
    }
}
