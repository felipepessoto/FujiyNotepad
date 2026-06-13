using System;
using System.Threading;
using System.Threading.Tasks;
using FujiyNotepad.UI.Model;

namespace FujiyNotepad.UI.Tests
{
    public class LineIndexerTests
    {
        private static async Task<LineIndexer> BuildIndexAsync(string content)
        {
            // The mapped file is only needed while indexing; the line offsets are cached in memory,
            // so it is safe to dispose the file before the assertions run.
            using var file = new TestMappedFile(content);
            var searcher = new TextSearcher(file.Mmf, file.Size);
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
            // Regression for the Stop-indexing fix: a cancelled token must surface as an
            // OperationCanceledException (so the UI re-enables resuming) and must not mark the
            // partial index complete.
            using var file = new TestMappedFile("a\nb\nc\nd\n");
            var searcher = new TextSearcher(file.Mmf, file.Size);
            var indexer = new LineIndexer(searcher);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => indexer.StartTaskToIndexLines(cts.Token, new Progress<int>()));
            Assert.False(indexer.IsCompleted);
        }
    }
}
