using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FujiyNotepad.Core;

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
    }
}
