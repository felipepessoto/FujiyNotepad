using System.Text.RegularExpressions;
using FujiyNotepad.Core;
using FujiyNotepad.TestSupport;

namespace FujiyNotepad.Presentation.Tests
{
    public class SeverityFilterTests
    {
        [Theory]
        [InlineData("TRACE", LogSeverity.Trace)]
        [InlineData("VERBOSE", LogSeverity.Trace)]
        [InlineData("DEBUG", LogSeverity.Debug)]
        [InlineData("INFO", LogSeverity.Info)]
        [InlineData("NOTICE", LogSeverity.Info)]
        [InlineData("WARN", LogSeverity.Warn)]
        [InlineData("WARNING", LogSeverity.Warn)]
        [InlineData("ERROR", LogSeverity.Error)]
        [InlineData("ERR", LogSeverity.Error)]
        [InlineData("SEVERE", LogSeverity.Error)]
        [InlineData("FATAL", LogSeverity.Fatal)]
        [InlineData("CRITICAL", LogSeverity.Fatal)]
        public void EveryFloor_MatchesOnlyTokensAtOrAboveIt(string token, LogSeverity severity)
        {
            foreach (LogSeverity floor in Enum.GetValues<LogSeverity>())
            {
                Regex regex = UserRegex.Create(SeverityFilter.GetPattern(floor), RegexOptions.None);
                bool expected = severity >= floor;

                Assert.Equal(expected, regex.IsMatch(token));
                Assert.Equal(expected, regex.IsMatch($"[{token.ToLowerInvariant()}] message"));
                Assert.Equal(expected, regex.IsMatch($"26/06/17 22:05:51 {token} [main] SparkContext: message"));
                Assert.Equal(expected, regex.IsMatch($"2026-06-17 22:05:51,123 - app.worker - {token} - message"));
                Assert.False(regex.Options.HasFlag(RegexOptions.Compiled));
                Assert.Equal(UserRegex.MatchTimeout, regex.MatchTimeout);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("INFO startup")]
        [InlineData("DEBUG detail")]
        [InlineData("WARNINGS and ERRORS")]
        [InlineData("preWARN ERROR_code FATALity")]
        [InlineData("    at app.Worker.Run()")]
        public void WarnFloor_DropsLowerLevelsAndNonTokens(string line)
        {
            Regex regex = UserRegex.Create(SeverityFilter.GetPattern(LogSeverity.Warn), RegexOptions.None);

            Assert.DoesNotMatch(regex, line);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(6)]
        public void InvalidFloor_Throws(int floor)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SeverityFilter.GetPattern((LogSeverity)floor));
        }

        [Fact]
        public void WarnFloor_FiltersLog4jAndPythonLines_KeepingOriginalLineMappingAndHighlights()
        {
            var source = new FakeLines(
                "26/06/17 22:05:51 INFO [main] SparkContext: Starting",
                "26/06/17 22:05:52 WARN [main] NativeCodeLoader: Unable to load",
                "26/06/17 22:05:53 DEBUG [worker] TaskSetManager: Starting task",
                "26/06/17 22:05:54 ERROR [worker] Executor: Task failed",
                "26/06/17 22:05:55 FATAL [main] SparkContext: Stopping",
                "2026-06-17 22:05:56,123 - app - WARNING - Retry",
                "2026-06-17 22:05:57,123 - app - CRITICAL - Stopping",
                "    at app.Worker.Run()",
                "2026-06-17 22:05:58,123 - app - INFO - Finished");
            Regex regex = UserRegex.Create(SeverityFilter.GetPattern(LogSeverity.Warn), RegexOptions.None);

            List<int> matches = LineFilter.Match(source, line => regex.IsMatch(line), out bool capped);
            var filtered = new FilteredLineSource(source, matches);
            HighlightPreset preset = HighlightPresets.All.Single(p => p.Name == "Log levels");
            HighlightRuleSet highlights = HighlightRuleSet.Build(HighlightRuleText.Parse(preset.RulesText));

            Assert.Equal(new[] { 1, 3, 4, 5, 6 }, matches);
            Assert.False(capped);
            Assert.Equal(matches.Count, filtered.LineCount);
            for (int row = 0; row < filtered.LineCount; row++)
            {
                Assert.Equal(matches[row], filtered.SourceLineAt(row));
                Assert.Equal(source.GetLine(matches[row]), filtered.GetLine(row));
                Assert.NotEmpty(highlights.Find(filtered.GetLine(row)));
            }
        }
    }
}
