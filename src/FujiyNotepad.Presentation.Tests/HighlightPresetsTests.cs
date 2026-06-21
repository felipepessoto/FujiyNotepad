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
        [InlineData("Spark / YARN", "26/06/17 22:05:51 INFO DAGScheduler: Job 5 finished: collect, took 12.345 s")]
        public void Preset_MatchesRepresentativeLine(string name, string sample)
        {
            HighlightPreset preset = HighlightPresets.All.Single(p => p.Name == name);
            HighlightRuleSet set = HighlightRuleSet.Build(HighlightRuleText.Parse(preset.RulesText));

            Assert.NotEmpty(set.Find(sample));
        }

        [Fact]
        public void SparkYarnPreset_HighlightsEventsAndDimsTokenNoise()
        {
            HighlightPreset preset = HighlightPresets.All.Single(p => p.Name == "Spark / YARN");
            HighlightRuleSet set = HighlightRuleSet.Build(HighlightRuleText.Parse(preset.RulesText));

            // Scheduler / lifecycle events are highlighted.
            Assert.NotEmpty(set.Find("26/06/17 22:05:51 INFO DAGScheduler: Job 5 finished: collect, took 12.345 s"));
            Assert.NotEmpty(set.Find("26/06/17 22:06:01 ERROR YarnScheduler: Lost executor 7 on host-1"));
            Assert.NotEmpty(set.Find("26/06/17 22:05:50 INFO DAGScheduler: Submitting ResultStage 3"));

            // The token / SAS boilerplate is dimmed in gray.
            Assert.True(HighlightColors.TryParse("gray", out uint gray));
            IReadOnlyList<HighlightSpan> tokenLine =
                set.Find("26/06/17 22:05:40 INFO TokenLibrary: refresh via SystemSASProviderWithRefresh");
            Assert.NotEmpty(tokenLine);
            Assert.Contains(tokenLine, s => s.Argb == gray);
        }
    }
}
