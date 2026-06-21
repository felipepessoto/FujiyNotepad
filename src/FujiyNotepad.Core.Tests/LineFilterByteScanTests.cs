using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FujiyNotepad.Core;
using FujiyNotepad.TestSupport;

namespace FujiyNotepad.Core.Tests
{
    /// <summary>
    /// Tests the byte-scan filter fast path (<see cref="LineFilter.MatchLinesByPatternAsync"/>): it returns the
    /// ascending, de-duplicated 0-based indices of the lines that contain the literal pattern — matching the
    /// decode path's results — with ASCII case-folding and capping. Built over an in-memory source and a real,
    /// fully-built <see cref="LineIndexer"/>.
    /// </summary>
    public class LineFilterByteScanTests
    {
        private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);

        private static async Task<(TextSearcher searcher, LineIndexer indexer)> BuildAsync(string content)
        {
            var source = new InMemoryByteSource(content);
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            return (searcher, indexer);
        }

        [Fact]
        public async Task MatchLinesByPattern_ReturnsMatchingLineIndices()
        {
            var (searcher, indexer) = await BuildAsync("INFO start\nERROR boom\nINFO tick\nERROR again\ndone");

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(searcher, indexer, Ascii("ERROR"), default);

            Assert.Equal(new[] { 1, 3 }, lines);
            Assert.False(capped);
        }

        [Fact]
        public async Task MatchLinesByPattern_NoMatch_ReturnsEmpty()
        {
            var (searcher, indexer) = await BuildAsync("a\nb\nc");

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(searcher, indexer, Ascii("zzz"), default);

            Assert.Empty(lines);
            Assert.False(capped);
        }

        [Fact]
        public async Task MatchLinesByPattern_MultipleMatchesOnOneLine_CountTheLineOnce()
        {
            // line 0 and line 2 each contain several 'x's; the dedupe must collapse each line to a single index.
            var (searcher, indexer) = await BuildAsync("x x x\ny\nxx");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(searcher, indexer, Ascii("x"), default);

            Assert.Equal(new[] { 0, 2 }, lines);
        }

        [Fact]
        public async Task MatchLinesByPattern_CapsAtMaxMatchesAndReportsCapped()
        {
            var (searcher, indexer) = await BuildAsync("x1\nx2\nx3\nx4\nx5");

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(searcher, indexer, Ascii("x"), default, maxMatches: 3);

            Assert.Equal(new[] { 0, 1, 2 }, lines);
            Assert.True(capped);
        }

        [Fact]
        public async Task MatchLinesByPattern_IgnoreCase_FoldsAsciiLetters()
        {
            var (searcher, indexer) = await BuildAsync("error one\nERROR two\nErRoR three\nok");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("error"), new SearchOptions { IgnoreCase = true });

            Assert.Equal(new[] { 0, 1, 2 }, lines);
        }

        [Fact]
        public async Task MatchLinesByPattern_CaseSensitive_MatchesExactCaseOnly()
        {
            var (searcher, indexer) = await BuildAsync("error one\nERROR two\nok");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(searcher, indexer, Ascii("ERROR"), default);

            Assert.Equal(new[] { 1 }, lines);
        }

        [Fact]
        public async Task MatchLinesByPattern_AgreesWithDecodePath_OnRepresentativeLog()
        {
            string content = string.Join('\n',
                "2026-06-17 INFO started",
                "2026-06-17 WARN slow",
                "2026-06-17 ERROR boom",
                "2026-06-17 INFO ok",
                "2026-06-17 ERROR again");
            var (searcher, indexer) = await BuildAsync(content);

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(searcher, indexer, Ascii("ERROR"), default);

            // Must match exactly what the per-line decode path (string.Contains, Ordinal) would return.
            var expected = new List<int>();
            string[] split = content.Split('\n');
            for (int i = 0; i < split.Length; i++)
            {
                if (split[i].Contains("ERROR", StringComparison.Ordinal))
                {
                    expected.Add(i);
                }
            }
            Assert.Equal(expected, lines);
        }

        [Fact]
        public async Task MatchLinesByPattern_FindsMatchBeyondPerLineDecodeCap()
        {
            // A line longer than LineProvider's 64 KB per-line decode cap, with the term only past that cap. The
            // byte scanner has no per-line cap (like Find), so it still matches the line — unlike the old per-line
            // decode filter, which truncated each line at 64 KB before testing and would miss this match.
            string longLine = new string('a', 70_000) + "NEEDLE";
            var (searcher, indexer) = await BuildAsync("short\n" + longLine + "\ntail");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(searcher, indexer, Ascii("NEEDLE"), default);

            Assert.Equal(new[] { 1 }, lines);
        }
    }
}
