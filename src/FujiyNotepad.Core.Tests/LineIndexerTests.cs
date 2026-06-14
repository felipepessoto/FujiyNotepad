using System;
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
    }
}
