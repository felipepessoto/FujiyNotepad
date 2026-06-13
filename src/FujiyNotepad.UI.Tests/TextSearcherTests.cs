using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FujiyNotepad.UI.Model;

namespace FujiyNotepad.UI.Tests
{
    public class TextSearcherTests
    {
        private static async Task<List<long>> SearchAllAsync(string content, long startOffset, string pattern, int? chunkSize = null, CancellationToken token = default)
        {
            var source = new InMemoryByteSource(content);
            var searcher = chunkSize is int cs ? new TextSearcher(source, cs) : new TextSearcher(source);
            var results = new List<long>();
            await foreach (long offset in searcher.Search(startOffset, Encoding.ASCII.GetBytes(pattern), token: token))
            {
                results.Add(offset);
            }
            return results;
        }

        [Fact]
        public async Task Search_SingleCharNewline_ReturnsEachNewlineOffset()
        {
            // "ab\ncd\nef" -> '\n' at offsets 2 and 5.
            var offsets = await SearchAllAsync("ab\ncd\nef", 0, "\n");
            Assert.Equal(new long[] { 2, 5 }, offsets);
        }

        [Fact]
        public async Task Search_MultiChar_ReturnsEachMatchStartOffset()
        {
            // "cdxcd" -> "cd" at offsets 0 and 3.
            var offsets = await SearchAllAsync("cdxcd", 0, "cd");
            Assert.Equal(new long[] { 0, 3 }, offsets);
        }

        [Fact]
        public async Task Search_NoMatch_ReturnsEmpty()
        {
            var offsets = await SearchAllAsync("abcdef", 0, "zz");
            Assert.Empty(offsets);
        }

        [Fact]
        public async Task Search_PatternTruncatedAtEof_DoesNotFalseMatch()
        {
            // The trailing 'a' (offset 3) starts the pattern but there is no following byte, so it
            // must not be reported as a match.
            var offsets = await SearchAllAsync("abxa", 0, "ab");
            Assert.Equal(new long[] { 0 }, offsets);
        }

        [Fact]
        public async Task Search_WithStartOffset_SkipsEarlierMatches()
        {
            // "a\nb\nc\n" -> '\n' at 1, 3, 5; starting at offset 2 skips the first.
            var offsets = await SearchAllAsync("a\nb\nc\n", 2, "\n");
            Assert.Equal(new long[] { 3, 5 }, offsets);
        }

        [Fact]
        public async Task Search_CancelledToken_StopsGracefullyWithoutThrowing()
        {
            // Search must never throw on cancellation (its async-void caller has no try/catch).
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var offsets = await SearchAllAsync("the quick brown fox\n", 0, "quick", token: cts.Token);
            Assert.Empty(offsets);
        }

        [Fact]
        public async Task Search_PatternSpanningChunkBoundary_IsFound()
        {
            // chunkSize 3 -> chunks [abc][def]; "cd" straddles the boundary (offset 2). The
            // carry-over of (pattern.Length - 1) bytes must let it be found.
            var offsets = await SearchAllAsync("abcdef", 0, "cd", chunkSize: 3);
            Assert.Equal(new long[] { 2 }, offsets);
        }

        [Fact]
        public async Task Search_ManyNewlinesAcrossChunks_AreAllFound()
        {
            // Tiny chunk forces many chunk iterations; every newline must still be reported once.
            var offsets = await SearchAllAsync("a\nb\nc\nd\n", 0, "\n", chunkSize: 2);
            Assert.Equal(new long[] { 1, 3, 5, 7 }, offsets);
        }

        [Fact]
        public void SearchBackward_Newline_ReturnsDescendingOffsetsThenFileStart()
        {
            // "a\nbc\nd" -> '\n' at 1 and 4. Backward scan yields 4, 1, then -1 (implicit start-of-file).
            var source = new InMemoryByteSource("a\nbc\nd");
            var searcher = new TextSearcher(source);
            var offsets = searcher.SearchBackward(source.Length, (byte)'\n').ToList();
            Assert.Equal(new long[] { 4, 1, -1 }, offsets);
        }

        [Fact]
        public void SearchBackward_AcrossChunks_ReturnsDescendingOffsets()
        {
            // Tiny chunk forces multiple backward blocks; order must stay strictly descending.
            var source = new InMemoryByteSource("a\nb\nc\nd");
            var searcher = new TextSearcher(source, chunkSize: 2);
            var offsets = searcher.SearchBackward(source.Length, (byte)'\n').ToList();
            Assert.Equal(new long[] { 5, 3, 1, -1 }, offsets);
        }

        [Fact]
        public void SearchBackward_FindsNearestPrecedingNewline()
        {
            var source = new InMemoryByteSource("a\nbc\nd");
            var searcher = new TextSearcher(source);
            // From offset 3 the nearest preceding '\n' is at offset 1.
            long nearest = searcher.SearchBackward(3, (byte)'\n').First();
            Assert.Equal(1, nearest);
        }
    }
}
