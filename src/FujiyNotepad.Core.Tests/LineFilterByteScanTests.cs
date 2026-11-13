using System;
using System.Collections.Generic;
using System.IO;
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
        private sealed class SyncProgress : IProgress<int>
        {
            private readonly Action<int> report;
            public SyncProgress(Action<int> report) => this.report = report;
            public void Report(int value) => report(value);
        }

        private sealed class ObservedByteSource : IByteSource
        {
            private readonly IByteSource inner;
            public ObservedByteSource(IByteSource inner) => this.inner = inner;
            public Action? BeforeRead { get; set; }
            public int Reads { get; private set; }
            public long Length => inner.Length;
            public long RefreshLength() => inner.RefreshLength();

            public int Read(long offset, Span<byte> buffer)
            {
                Reads++;
                BeforeRead?.Invoke();
                return inner.Read(offset, buffer);
            }

            public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
                => ValueTask.FromResult(Read(offset, buffer.Span));

            public void Dispose() => inner.Dispose();
        }

        private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);

        private static async Task<(TextSearcher searcher, LineIndexer indexer)> BuildAsync(string content)
        {
            var source = new InMemoryByteSource(content);
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            return (searcher, indexer);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_ReturnsMatchingLineIndicesOrTheirComplement(bool exclude)
        {
            var (searcher, indexer) = await BuildAsync("INFO start\nERROR boom\nINFO tick\nERROR again\ndone");

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("ERROR"), default, exclude: exclude);

            Assert.Equal(exclude ? new[] { 0, 2, 4 } : new[] { 1, 3 }, lines);
            Assert.False(capped);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_NoMatch_ReturnsEmptyOrAllLines(bool exclude)
        {
            var (searcher, indexer) = await BuildAsync("a\nb\nc");

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("zzz"), default, exclude: exclude);

            Assert.Equal(exclude ? new[] { 0, 1, 2 } : Array.Empty<int>(), lines);
            Assert.False(capped);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_MultipleMatchesOnOneLine_CountTheLineOnce(bool exclude)
        {
            // line 0 and line 2 each contain several 'x's; the dedupe must collapse each line to a single index.
            var (searcher, indexer) = await BuildAsync("x x x\ny\nxx");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("x"), default, exclude: exclude);

            Assert.Equal(exclude ? new[] { 1 } : new[] { 0, 2 }, lines);
        }

        [Fact]
        public async Task MatchLinesByPattern_CapsAtMaxMatchesAndReportsCapped()
        {
            var (searcher, indexer) = await BuildAsync("x1\nx2\nx3\nx4\nx5");

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(searcher, indexer, Ascii("x"), default, maxMatches: 3);

            Assert.Equal(new[] { 0, 1, 2 }, lines);
            Assert.True(capped);
        }

        [Theory]
        [InlineData("x\nx\nx\nkeep\nx\nkeep\nkeep", new[] { 3, 5 })]
        [InlineData("keep\nkeep\nkeep\nx", new[] { 0, 1 })]
        [InlineData("keep\nkeep\nkeep", new[] { 0, 1 })]
        public async Task MatchLinesByPattern_Exclude_CapsKeptLinesNotDiscardedMatches(string content, int[] expected)
        {
            var (searcher, indexer) = await BuildAsync(content);

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("x"), default, maxMatches: 2, exclude: true);

            Assert.Equal(expected, lines);
            Assert.True(capped);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_IgnoreCase_FoldsAsciiLetters(bool exclude)
        {
            var (searcher, indexer) = await BuildAsync("error one\nERROR two\nErRoR three\nok");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("error"), new SearchOptions { IgnoreCase = true }, exclude: exclude);

            Assert.Equal(exclude ? new[] { 3 } : new[] { 0, 1, 2 }, lines);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_CaseSensitive_MatchesExactCaseOnly(bool exclude)
        {
            var (searcher, indexer) = await BuildAsync("error one\nERROR two\nok");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("ERROR"), default, exclude: exclude);

            Assert.Equal(exclude ? new[] { 0, 2 } : new[] { 1 }, lines);
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_FindsMatchBeyondPerLineDecodeCap(bool exclude)
        {
            // A line longer than LineProvider's 64 KB per-line decode cap, with the term only past that cap. The
            // byte scanner has no per-line cap (like Find), so it still matches the line — unlike the old per-line
            // decode filter, which truncated each line at 64 KB before testing and would miss this match.
            string longLine = new string('a', 70_000) + "NEEDLE";
            var (searcher, indexer) = await BuildAsync("short\n" + longLine + "\ntail");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("NEEDLE"), default, exclude: exclude);

            Assert.Equal(exclude ? new[] { 0, 2 } : new[] { 1 }, lines);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_WholeWord_MatchesBoundedTermOnly(bool exclude)
        {
            var (searcher, indexer) = await BuildAsync("error\nerrors\nan error here\nterror\nERROR caps\n(error)");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("error"), new SearchOptions { WholeWord = true }, exclude: exclude);

            // "errors" (trailing 's') and "terror" (leading 't') are not whole-word matches; the caps line is
            // dropped by the case-sensitive default; "(error)" matches since '(' and ')' are word boundaries.
            Assert.Equal(exclude ? new[] { 1, 3, 4 } : new[] { 0, 2, 5 }, lines);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_WholeWord_IgnoreCase_FoldsButStillBounded(bool exclude)
        {
            var (searcher, indexer) = await BuildAsync("error\nERROR\nerrors\nMyError");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("error"), new SearchOptions { WholeWord = true, IgnoreCase = true }, exclude: exclude);

            // Case-folded so ERROR matches, but "errors" (trailing 's') and "MyError" (leading word char) don't.
            Assert.Equal(exclude ? new[] { 2, 3 } : new[] { 0, 1 }, lines);
        }

        [Theory]
        [InlineData("", 0)]
        [InlineData("plain", 1)]
        [InlineData("plain\n", 1)]
        [InlineData("plain\r\n", 1)]
        [InlineData("\n\n", 2)]
        [InlineData("plain\n\ntail", 3)]
        public async Task MatchLinesByPattern_Exclude_NoMatch_DoesNotInventTrailingRows(string content, int count)
        {
            var (searcher, indexer) = await BuildAsync(content);

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("absent"), default, exclude: true);

            Assert.Equal(Enumerable.Range(0, count), lines);
            Assert.False(capped);
        }

        [Fact]
        public async Task MatchLinesByPattern_Exclude_AllLinesMatch_ReturnsEmpty()
        {
            var (searcher, indexer) = await BuildAsync("x\nx\nx");

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("x"), default, maxMatches: 1, exclude: true);

            Assert.Empty(lines);
            Assert.False(capped);
        }

        [Fact]
        public async Task MatchLinesByPattern_Exclude_PreCancelled_DoesNotReturnTheWholeFile()
        {
            var (searcher, indexer) = await BuildAsync("keep\nERROR\nkeep");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("ERROR"), default, token: cts.Token, exclude: true);

            Assert.Empty(lines);
            Assert.False(capped);
        }

        [Fact]
        public async Task MatchLinesByPattern_Exclude_CancelledBetweenChunks_DoesNotComplementTheUnscannedTail()
        {
            using var source = new InMemoryByteSource("keep\nERROR\nkeep\nERROR\nkeep\n");
            var searcher = new TextSearcher(source, chunkSize: 16);
            var indexer = new LineIndexer(searcher);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            using var cts = new CancellationTokenSource();
            var progress = new SyncProgress(p =>
            {
                if (p > 0)
                {
                    cts.Cancel();
                }
            });

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("ERROR"), default, progress: progress, token: cts.Token, exclude: true);

            Assert.True(cts.IsCancellationRequested);
            Assert.Equal(new[] { 0 }, lines);
            Assert.False(capped);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_PreCancelled_DoesNotReadAnUncachedLongLine(bool exclude)
        {
            using var source = new ObservedByteSource(new InMemoryByteSource(new string('x', 8192) + "\n"));
            var searcher = new TextSearcher(source, chunkSize: 64);
            var indexer = new LineIndexer(searcher);
            indexer.SetPartialIndexForTest(8193);
            indexer.IsCompleted = true;
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("absent"), default, token: cts.Token, exclude: exclude);

            Assert.Empty(lines);
            Assert.False(capped);
            Assert.Equal(0, source.Reads);
        }

        [Fact]
        public async Task MatchLinesByPattern_Exclude_CancelledDuringSnapshotLookup_StopsReading()
        {
            using var source = new ObservedByteSource(new InMemoryByteSource(new string('x', 8192) + "\n"));
            var searcher = new TextSearcher(source, chunkSize: 64);
            var indexer = new LineIndexer(searcher);
            indexer.SetPartialIndexForTest(8193);
            indexer.IsCompleted = true;
            using var cts = new CancellationTokenSource();
            source.BeforeRead = () =>
            {
                if (source.Reads == 3)
                {
                    cts.Cancel();
                }
            };

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("absent"), default, token: cts.Token, exclude: true);

            Assert.Empty(lines);
            Assert.False(capped);
            Assert.Equal(3, source.Reads);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_CancelledDuringRead_DoesNotCollectBufferedMatches(bool exclude)
        {
            using var source = new ObservedByteSource(new InMemoryByteSource("keep\nx\nkeep\nx\n"));
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            indexer.SetPartialIndexForTest(5, 7, 12, 14);
            indexer.IsCompleted = true;
            Assert.Equal(3, indexer.GetLineNumberFromOffset(source.Length - 1));
            int readsBeforeScan = source.Reads;
            using var cts = new CancellationTokenSource();
            source.BeforeRead = cts.Cancel;

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("x"), default, token: cts.Token, exclude: exclude);

            Assert.Empty(lines);
            Assert.False(capped);
            Assert.Equal(readsBeforeScan + 1, source.Reads);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(5)]
        public async Task MatchLinesByPattern_Exclude_TruncatedSnapshot_ThrowsInsteadOfReturningOldRows(int remainingBytes)
        {
            using var source = new GrowableByteSource("keep\nERROR\nother\n");
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            var progress = new SyncProgress(p =>
            {
                if (p == 0)
                {
                    source.Truncate(remainingBytes);
                }
            });

            await Assert.ThrowsAsync<EndOfStreamException>(() =>
                LineFilter.MatchLinesByPatternAsync(
                    searcher, indexer, Ascii("ERROR"), default, progress: progress, exclude: true));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_TruncatedBetweenChunks_DoesNotComplementTheUnscannedTail(bool exclude)
        {
            using var source = new GrowableByteSource("keep\nERROR\nkeep\nERROR\nkeep\n");
            var searcher = new TextSearcher(source, chunkSize: 16);
            var indexer = new LineIndexer(searcher);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            var progress = new SyncProgress(p =>
            {
                if (p > 0)
                {
                    source.Truncate(16);
                }
            });

            var scan = LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("ERROR"), default, progress: progress, exclude: exclude);

            if (exclude)
            {
                await Assert.ThrowsAsync<EndOfStreamException>(() => scan);
            }
            else
            {
                var (lines, capped) = await scan;
                Assert.Equal(new[] { 1 }, lines);
                Assert.False(capped);
            }
        }

        [Fact]
        public async Task MatchLinesByPattern_Exclude_LengthShrinksDuringSnapshotLookup_Throws()
        {
            var data = new GrowableByteSource("keep\nERROR\nother\n");
            using var source = new ObservedByteSource(data);
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            source.BeforeRead = () =>
            {
                source.BeforeRead = null;
                data.Truncate(5);
                data.RefreshLength();
            };

            await Assert.ThrowsAsync<EndOfStreamException>(() =>
                LineFilter.MatchLinesByPatternAsync(searcher, indexer, Ascii("ERROR"), default, exclude: true));
        }

        // ----- The scan must not map offsets the index does not cover (issue #169) -----

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_FileGrewAfterIndexing_DoesNotEmitClampedLines(bool exclude)
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

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("ERROR"), default, exclude: exclude);

            // Only the two indexed matches. The appended ones used to clamp onto line 3 — a row that does not
            // contain the term and that is past the provider's line count (the file has 3 lines, 0..2).
            Assert.Equal(exclude ? new[] { 1 } : new[] { 0, 2 }, lines);
            Assert.DoesNotContain(3, lines);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_MatchOnTheFinalUnterminatedLine_IsStillReturned(bool exclude)
        {
            // The bound must not cut off legitimate matches on the last line: the file ends without a newline,
            // so the final line's match sits between the last line start and the captured length.
            var (searcher, indexer) = await BuildAsync("plain one\nERROR last");

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("ERROR"), default, exclude: exclude);

            Assert.Equal(exclude ? new[] { 0 } : new[] { 1 }, lines);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_IndexNotComplete_StopsAtTheFrontierInsteadOfClamping(bool exclude)
        {
            // If the index is still building (the caller's IsCompleted check can lose a race with the Follow
            // Tail re-arm), the scan is bounded by the frontier rather than clamping everything past it.
            var source = new InMemoryByteSource("ERROR a\nplain b\nERROR c\nERROR d\n");
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);

            // Line starts 0, 8, 16, 24; frontier stops at 16, so the match at 24 is not yet resolvable.
            indexer.SetPartialIndexForTest(8, 16);
            Assert.False(indexer.IsCompleted);

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("ERROR"), default, exclude: exclude);

            Assert.Equal(exclude ? new[] { 1 } : new[] { 0 }, lines); // nothing clamped onto line 2
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_IndexRanAheadOfTheCachedLength_OmitsRatherThanMisattributes(bool exclude)
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

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("ERROR"), default, exclude: exclude);

            Assert.Equal(exclude ? Array.Empty<int>() : new[] { 0 }, lines); // line 1 sits past the cached length
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MatchLinesByPattern_FollowTailRearmsDuringScan_KeepsTheOriginalSnapshot(bool exclude)
        {
            using var source = new GrowableByteSource("keep\nERROR\n");
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            var progress = new SyncProgress(p =>
            {
                if (p == 0)
                {
                    source.Append("new keep\nERROR\n");
                    source.RefreshLength();
                    indexer.SetPartialIndexForTest(5, 11, 20, 26);
                }
            });

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("ERROR"), default, progress: progress, exclude: exclude);

            Assert.Equal(exclude ? new[] { 0 } : new[] { 1 }, lines);
            Assert.False(capped);
        }

        [Fact]
        public async Task MatchLinesByPattern_Exclude_NoCompletedLines_ReturnsEmpty()
        {
            using var source = new InMemoryByteSource("keep\nERROR\n");
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            indexer.SetPartialIndexForTest();

            var (lines, capped) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, Ascii("ERROR"), default, exclude: true);

            Assert.Empty(lines);
            Assert.False(capped);
        }
    }
}
