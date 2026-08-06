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

        [Fact]
        public async Task MatchLinesByPattern_WholeWord_MatchesBoundedTermOnly()
        {
            var (searcher, indexer) = await BuildAsync("error\nerrors\nan error here\nterror\nERROR caps\n(error)");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("error"), new SearchOptions { WholeWord = true });

            // "errors" (trailing 's') and "terror" (leading 't') are not whole-word matches; the caps line is
            // dropped by the case-sensitive default; "(error)" matches since '(' and ')' are word boundaries.
            Assert.Equal(new[] { 0, 2, 5 }, lines);
        }

        [Fact]
        public async Task MatchLinesByPattern_WholeWord_IgnoreCase_FoldsButStillBounded()
        {
            var (searcher, indexer) = await BuildAsync("error\nERROR\nerrors\nMyError");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("error"), new SearchOptions { WholeWord = true, IgnoreCase = true });

            // Case-folded so ERROR matches, but "errors" (trailing 's') and "MyError" (leading word char) don't.
            Assert.Equal(new[] { 0, 1 }, lines);
        }

        // ----- The scan must not map offsets the index does not cover (issue #169) -----

        [Fact]
        public async Task MatchLinesByPattern_FileGrewAfterIndexing_DoesNotEmitClampedLines()
        {
            // "ERROR one\nplain two\nERROR three\n" = 32 bytes; line starts 0, 10, 20, 32; "ERROR" at 0 and 20.
            var source = new GrowableByteSource("ERROR one\nplain two\nERROR three\n");
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            Assert.True(indexer.IsCompleted);

            // A writer appends after indexing completed. Length is NOT refreshed (nothing called RefreshLength),
            // exactly like a file growing while Follow Tail is off — but Search reads to the live end of file,
            // so it will still find these matches.
            source.Append("ERROR four\nERROR five\n");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(searcher, indexer, Ascii("ERROR"), default);

            // Only the two indexed matches. The appended ones used to clamp onto line 3 — a row that does not
            // contain the term and that is past the provider's line count (the file has 3 lines, 0..2).
            Assert.Equal(new[] { 0, 2 }, lines);
            Assert.DoesNotContain(3, lines);
        }

        [Fact]
        public async Task MatchLinesByPattern_MatchOnTheFinalUnterminatedLine_IsStillReturned()
        {
            // The bound must not cut off legitimate matches on the last line: the file ends without a newline,
            // so the final line's match sits between the last line start and the captured length.
            var (searcher, indexer) = await BuildAsync("plain one\nERROR last");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(searcher, indexer, Ascii("ERROR"), default);

            Assert.Equal(new[] { 1 }, lines);
        }

        [Fact]
        public async Task MatchLinesByPattern_IndexNotComplete_StopsAtTheFrontierInsteadOfClamping()
        {
            // If the index is still building (the caller's IsCompleted check can lose a race with the Follow
            // Tail re-arm), the scan is bounded by the frontier rather than clamping everything past it.
            var source = new InMemoryByteSource("ERROR a\nplain b\nERROR c\nERROR d\n");
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);

            // Line starts 0, 8, 16, 24; frontier stops at 16, so the match at 24 is not yet resolvable.
            indexer.SetPartialIndexForTest(8, 16);
            Assert.False(indexer.IsCompleted);

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(searcher, indexer, Ascii("ERROR"), default);

            Assert.Equal(new[] { 0 }, lines); // only the match below the frontier; nothing clamped onto line 2
        }

        [Fact]
        public async Task MatchLinesByPattern_IndexRanAheadOfTheCachedLength_OmitsRatherThanMisattributes()
        {
            // Documents a deliberate conservative edge. Indexing reads to the LIVE end of file, but the source's
            // cached length only moves on an explicit RefreshLength — which the app issues only while Follow Tail
            // is on. So a file that grows while indexing runs can end up indexed past its cached length.
            //
            // The bound is the cached length, so those matches are omitted rather than shown. That is the safe
            // direction: the alternative is reading past the bound, which is exactly what lets a growing file
            // contribute matches the index cannot place. If this is ever tightened, tighten it knowingly.
            var source = new GrowableByteSource("ERROR one\n");
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);

            source.Append("ERROR two\n"); // present on disk before indexing, but Length still reports 10
            Assert.Equal(10, source.Length);

            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            Assert.True(indexer.IsCompleted);
            Assert.Equal(4, indexer.GetNumberOfLinesIndexed()); // the index DID see both lines (starts 0, 10, 20)

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(searcher, indexer, Ascii("ERROR"), default);

            Assert.Equal(new[] { 0 }, lines); // line 1 is indexed, but sits past the cached length
        }
    }
}
