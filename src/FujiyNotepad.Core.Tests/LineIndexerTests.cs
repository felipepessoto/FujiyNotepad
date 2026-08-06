using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FujiyNotepad.Core;
using FujiyNotepad.TestSupport;

namespace FujiyNotepad.Core.Tests
{
    public class LineIndexerTests
    {
        private static async Task<LineIndexer> BuildIndexAsync(string content)
        {
            var source = new InMemoryByteSource(content);
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            return indexer;
        }

        [Fact]
        public async Task StartTaskToIndexLines_MapsLineNumbersToOffsets()
        {
            // "ab\ncd\nef" -> '\n' at 2 and 5; lines start at offsets 0, 3, 6.
            var indexer = await BuildIndexAsync("ab\ncd\nef");

            Assert.True(indexer.IsCompleted);
            Assert.Equal(4, indexer.GetNumberOfLinesIndexed()); // index = [0, 0, 3, 6]
            Assert.Equal(0, indexer.GetOffsetFromLineNumber(1));
            Assert.Equal(3, indexer.GetOffsetFromLineNumber(2));
            Assert.Equal(6, indexer.GetOffsetFromLineNumber(3));
        }

        [Fact]
        public async Task GetOffsetFromLineNumber_OutOfRange_Throws()
        {
            var indexer = await BuildIndexAsync("ab\ncd");
            Assert.Throws<InvalidOperationException>(() => indexer.GetOffsetFromLineNumber(100));
        }

        [Fact]
        public async Task StartTaskToIndexLines_CancelledToken_ThrowsAndDoesNotComplete()
        {
            var source = new InMemoryByteSource("a\nb\nc\nd\n");
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => indexer.StartTaskToIndexLines(cts.Token, new Progress<int>()));
            Assert.False(indexer.IsCompleted);
        }

        [Fact]
        public async Task GetLineNumberFromOffset_MapsOffsetsToLines()
        {
            // "ab\ncd\nef": lines start at 0, 3, 6; the '\n' bytes (2 and 5) belong to the line they end.
            var indexer = await BuildIndexAsync("ab\ncd\nef");

            Assert.Equal(0, indexer.GetLineNumberFromOffset(0));
            Assert.Equal(0, indexer.GetLineNumberFromOffset(2));
            Assert.Equal(1, indexer.GetLineNumberFromOffset(3));
            Assert.Equal(1, indexer.GetLineNumberFromOffset(5));
            Assert.Equal(2, indexer.GetLineNumberFromOffset(6));
            Assert.Equal(2, indexer.GetLineNumberFromOffset(7));
        }

        // ----- Sparse index across multiple checkpoint blocks (CheckpointInterval = 1024) -----

        [Fact]
        public async Task SparseIndex_FixedWidthAcrossBlocks_ResolvesExactOffsets()
        {
            // 3000 fixed-width lines joined by '\n' (no trailing newline) span ~3 checkpoint blocks, so
            // reconstruction must binary-search the checkpoints and scan within a block, not read a flat array.
            // Each line is "lineNNNNNN" (10 bytes) + a '\n' separator => an 11-byte stride; line i starts at 11i.
            const int count = 3000;
            const int stride = 11;
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sb.Append('\n');
                }
                sb.Append($"line{i:D6}");
            }

            var indexer = await BuildIndexAsync(sb.ToString());

            Assert.Equal(count + 1, indexer.GetNumberOfLinesIndexed()); // dummy [0] + one start per line

            foreach (int i in new[] { 0, 1, 1023, 1024, 1025, 2047, 2048, 2049, 2999 })
            {
                long start = (long)i * stride;
                Assert.Equal(start, indexer.GetOffsetFromLineNumber(i + 1));  // exact start of line i
                Assert.Equal(i, indexer.GetLineNumberFromOffset(start));      // offset at the start -> line i
                Assert.Equal(i, indexer.GetLineNumberFromOffset(start + 3));  // mid-line stays on line i
            }
        }

        [Fact]
        public async Task SparseIndex_VariableWidthAcrossBlocks_RoundTrips()
        {
            // Variable-length lines so reconstruction depends on the actual newline scan, not arithmetic.
            const int count = 2500;
            var sb = new StringBuilder();
            var starts = new List<long>(count);
            long pos = 0;
            var rnd = new Random(7);
            for (int i = 0; i < count; i++)
            {
                starts.Add(pos);
                int len = rnd.Next(1, 30);
                sb.Append(new string((char)('a' + (i % 26)), len)).Append('\n');
                pos += len + 1;
            }

            var indexer = await BuildIndexAsync(sb.ToString());

            foreach (int i in new[] { 0, 1, 1023, 1024, 1025, 2048, 2499 })
            {
                long start = starts[i];
                Assert.Equal(start, indexer.GetOffsetFromLineNumber(i + 1));
                Assert.Equal(i, indexer.GetLineNumberFromOffset(start));
                Assert.Equal(i, indexer.GetLineNumberFromOffset(start + 1)); // one byte into line i
            }
        }

        [Fact]
        public async Task SparseIndex_OutOfRangeAcrossBlocks_Throws()
        {
            // 2500 "x\n" lines (trailing newline) -> 2500 newlines + the seed = 2501 line starts, plus the
            // dummy [0] = 2502 valid entries (0..2501); entry 2502 is one past the end and throws.
            var sb = new StringBuilder();
            for (int i = 0; i < 2500; i++)
            {
                sb.Append("x\n");
            }
            var indexer = await BuildIndexAsync(sb.ToString());

            Assert.Equal(0L, indexer.GetOffsetFromLineNumber(1));            // line 0 starts at 0
            Assert.Equal(2L * 2499, indexer.GetOffsetFromLineNumber(2500));  // line 2499 starts at 4998
            Assert.Throws<InvalidOperationException>(() => indexer.GetOffsetFromLineNumber(2502));
        }

        private static LineIndexer NewIndexer() =>
            new LineIndexer(new TextSearcher(new InMemoryByteSource("")));

        [Fact]
        public void CanResolveOffset_NotCompleted_OffsetBeforeFrontier_IsTrue()
        {
            var indexer = NewIndexer();
            indexer.SetPartialIndexForTest(10, 20, 30); // index = [0, 0, 10, 20, 30], frontier = 30

            Assert.True(indexer.CanResolveOffset(0));
            Assert.True(indexer.CanResolveOffset(25));
            Assert.True(indexer.CanResolveOffset(29));
        }

        [Fact]
        public void CanResolveOffset_NotCompleted_OffsetAtOrBeyondFrontier_IsFalse()
        {
            var indexer = NewIndexer();
            indexer.SetPartialIndexForTest(10, 20, 30);

            Assert.False(indexer.CanResolveOffset(30));            // at the frontier: last line's end unknown
            Assert.False(indexer.CanResolveOffset(1_000_000_000)); // far beyond the indexed region
        }

        [Fact]
        public void CanResolveOffset_SeedOnly_IsFalse()
        {
            var indexer = NewIndexer();
            indexer.SetPartialIndexForTest(); // only the [0, 0] seed; nothing reliably indexed yet

            Assert.False(indexer.CanResolveOffset(0));
            Assert.False(indexer.CanResolveOffset(5));
        }

        [Fact]
        public void CanResolveOffset_Completed_IsAlwaysTrue()
        {
            var indexer = NewIndexer();
            indexer.SetPartialIndexForTest(10, 20, 30);
            indexer.IsCompleted = true;

            Assert.True(indexer.CanResolveOffset(1_000_000_000));
        }

        // ----- Indexing lifecycle: cancellation, single-writer, truncation (issues #165/#166/#167) -----

        [Fact]
        public async Task StartTaskToIndexLines_CancelledInsideNewlineFreeRegion_StopsPromptlyWithoutCompleting()
        {
            // A newline-free source yields nothing, so the in-loop cancellation check never runs: before the
            // token was forwarded to the scan (#167), the pass ran to EOF and "Stop indexing" was a no-op.
            // 8 KiB over a 64-byte chunk is 128 chunks; cancelling on the 3rd read must stop within a chunk
            // or two of that, not scan them all.
            var source = new CancelAfterReadsSource(size: 8192, cancelAfterReads: 3);
            using var cts = new CancellationTokenSource();
            source.Attach(cts);
            var indexer = new LineIndexer(new TextSearcher(source, chunkSize: 64));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => indexer.StartTaskToIndexLines(cts.Token, new Progress<int>()));

            Assert.False(indexer.IsCompleted); // a cancelled pass must never publish a partial index as complete
            Assert.InRange(source.Reads, 1, 10);
        }

        [Fact]
        public async Task StartTaskToIndexLines_SecondConcurrentPass_IsRejected()
        {
            // Two passes writing the same index interleave their appends: lineStartCount inflates, lastLineStart
            // can move backwards and checkpoints stop ascending, breaking the binary searches (#166).
            var source = new GatedByteSource("a\nb\nc\n");
            var indexer = new LineIndexer(new TextSearcher(source));

            Task first = indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            await source.Entered; // the first pass is now inside the scan, holding the writer slot

            // The guard rejects synchronously, so this is already faulted. The timeout only matters if the
            // guard ever regresses: without it the second pass would block on the gate and hang the suite
            // instead of failing.
            Task second = indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => second.WaitAsync(TimeSpan.FromSeconds(30)));

            source.Release();
            await first;
            Assert.True(indexer.IsCompleted);
            Assert.Equal(5, indexer.GetNumberOfLinesIndexed()); // dummy + starts 0, 2, 4, 6 — no doubled entries
        }

        [Fact]
        public async Task StartTaskToIndexLines_SequentialPasses_AreAllowed()
        {
            // The writer slot is released when a pass ends, so Follow Tail's resume (grow -> index the appended
            // region) still works. A one-shot latch here would break tailing outright.
            var source = new GrowableByteSource("a\nb\n");
            var indexer = new LineIndexer(new TextSearcher(source));

            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            Assert.Equal(4, indexer.GetNumberOfLinesIndexed()); // dummy + starts 0, 2, 4

            source.Append("c\nd\n");
            source.RefreshLength();
            indexer.IsCompleted = false;

            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());

            Assert.True(indexer.IsCompleted);
            Assert.Equal(6, indexer.GetNumberOfLinesIndexed()); // dummy + starts 0, 2, 4, 6, 8
        }

        [Fact]
        public async Task StartTaskToIndexLines_AfterCancellation_CanBeRestarted()
        {
            // The slot is released in a finally, so a cancelled pass doesn't wedge the indexer permanently —
            // this is exactly the Stop-then-Start indexing sequence.
            var source = new InMemoryByteSource("a\nb\nc\nd\n");
            var indexer = new LineIndexer(new TextSearcher(source));
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => indexer.StartTaskToIndexLines(cts.Token, new Progress<int>()));
            }

            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());

            Assert.True(indexer.IsCompleted);
            Assert.Equal(6, indexer.GetNumberOfLinesIndexed());
        }

        [Fact]
        public async Task GetOffsetFromLineNumber_SourceTruncatedAfterIndexing_ClampsInsteadOfThrowing()
        {
            // "aaa\nbbb\nccc\nddd\neee\n" -> line starts 0, 4, 8, 12, 16, 20.
            var source = new GrowableByteSource("aaa\nbbb\nccc\nddd\neee\n");
            var indexer = new LineIndexer(new TextSearcher(source));
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            Assert.Equal(7, indexer.GetNumberOfLinesIndexed()); // dummy + 6 starts; no lookup yet, so no cached block

            // An external rotation truncates the file in place (logrotate copytruncate / "> app.log"), which the
            // viewer supports. The index still describes the old, longer content, so expanding the block now
            // finds fewer newlines and returns a short array — which used to be indexed unchecked (#165).
            source.Truncate(8); // "aaa\nbbb\n"

            long offset = indexer.GetOffsetFromLineNumber(6);

            Assert.Equal(8L, offset); // clamped to the last line start that still exists
        }

        [Fact]
        public async Task GetOffsetFromLineNumber_SourceTruncatedAfterIndexing_StillResolvesSurvivingLines()
        {
            // Lines that still exist after the truncation must keep resolving exactly; only the ones past the
            // new end clamp. Guards against the clamp masking real lookups.
            var source = new GrowableByteSource("aaa\nbbb\nccc\nddd\neee\n");
            var indexer = new LineIndexer(new TextSearcher(source));
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());

            source.Truncate(8); // "aaa\nbbb\n"

            Assert.Equal(0L, indexer.GetOffsetFromLineNumber(1)); // line 0 — unaffected
            Assert.Equal(4L, indexer.GetOffsetFromLineNumber(2)); // line 1 — unaffected
            Assert.Equal(8L, indexer.GetOffsetFromLineNumber(3)); // the new frontier
        }

        [Fact]
        public async Task GetOffsetFromLineNumber_OutOfRange_StillThrowsAfterClampWasAdded()
        {
            // The clamp only covers a shrunken source; a line number outside the index is still a caller bug.
            var indexer = await BuildIndexAsync("ab\ncd");
            Assert.Throws<InvalidOperationException>(() => indexer.GetOffsetFromLineNumber(50));
        }

        // Counts reads and trips the token once the scan is under way, so cancellation lands between chunks
        // inside a newline-free region — the case where nothing is ever yielded to the indexing loop.
        private sealed class CancelAfterReadsSource : IByteSource
        {
            private readonly byte[] data;
            private readonly int cancelAfterReads;
            private CancellationTokenSource? cts;
            private int reads;

            public CancelAfterReadsSource(int size, int cancelAfterReads)
            {
                data = new byte[size];
                Array.Fill(data, (byte)'x'); // deliberately no newlines
                this.cancelAfterReads = cancelAfterReads;
            }

            public int Reads => Volatile.Read(ref reads);

            public void Attach(CancellationTokenSource tokenSource) => cts = tokenSource;

            public long Length => data.Length;

            public long RefreshLength() => data.Length;

            public int Read(long offset, Span<byte> buffer)
            {
                if (Interlocked.Increment(ref reads) >= cancelAfterReads)
                {
                    cts?.Cancel();
                }
                if (offset < 0 || offset >= data.Length)
                {
                    return 0;
                }
                int count = (int)Math.Min(buffer.Length, data.Length - offset);
                data.AsSpan((int)offset, count).CopyTo(buffer);
                return count;
            }

            public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
                => ValueTask.FromResult(Read(offset, buffer.Span));

            public void Dispose() { }
        }

        // Blocks the first read until released, so a pass can be held mid-scan while a second one is attempted.
        private sealed class GatedByteSource : IByteSource
        {
            private readonly byte[] data;
            private readonly TaskCompletionSource gate =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource entered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public GatedByteSource(string ascii) => data = Encoding.ASCII.GetBytes(ascii);

            /// <summary>Completes once the scan has entered its first read.</summary>
            public Task Entered => entered.Task;

            public void Release() => gate.TrySetResult();

            public long Length => data.Length;

            public long RefreshLength() => data.Length;

            public int Read(long offset, Span<byte> buffer)
            {
                if (offset < 0 || offset >= data.Length)
                {
                    return 0;
                }
                int count = (int)Math.Min(buffer.Length, data.Length - offset);
                data.AsSpan((int)offset, count).CopyTo(buffer);
                return count;
            }

            public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
            {
                entered.TrySetResult();
                await gate.Task;
                return Read(offset, buffer.Span);
            }

            public void Dispose() { }
        }
    }
}
