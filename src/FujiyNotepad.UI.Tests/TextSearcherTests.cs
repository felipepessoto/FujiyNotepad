using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FujiyNotepad.UI.Model;

namespace FujiyNotepad.UI.Tests
{
    public class TextSearcherTests
    {
        private static async Task<List<long>> SearchAllAsync(string content, long startOffset, string pattern, CancellationToken token = default)
        {
            using var file = new TestMappedFile(content);
            var searcher = new TextSearcher(file.Mmf, file.Size);
            var results = new List<long>();
            await foreach (long offset in searcher.Search(startOffset, pattern.ToCharArray(), new Progress<int>(), token))
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
            // Regression for the read-fully (CA2022) fix. After matching "ab" at offset 0 the shared
            // buffer holds 'b'. At the trailing 'a' (offset 3) only one byte remains, so the second
            // byte cannot be read; the old code compared the stale 'b' and reported a false match at 3.
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
            // Regression for the Find-cancel fix: Search must never throw on cancellation (its
            // async-void caller has no try/catch). A pre-cancelled token stops after one byte.
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var offsets = await SearchAllAsync("the quick brown fox\n", 0, "quick", cts.Token);
            Assert.Empty(offsets);
        }

        [Fact]
        public void SearchBackward_Newline_ReturnsDescendingOffsetsThenFileStart()
        {
            // "a\nbc\nd" -> '\n' at 1 and 4. Backward scan yields 4, 1, then -1 (implicit start-of-file).
            using var file = new TestMappedFile("a\nbc\nd");
            var searcher = new TextSearcher(file.Mmf, file.Size);
            var offsets = searcher.SearchBackward(file.Size, '\n', new Progress<int>()).ToList();
            Assert.Equal(new long[] { 4, 1, -1 }, offsets);
        }

        [Fact]
        public void SearchBackward_FindsNearestPrecedingNewline()
        {
            using var file = new TestMappedFile("a\nbc\nd");
            var searcher = new TextSearcher(file.Mmf, file.Size);
            // From offset 3 the nearest preceding '\n' is at offset 1.
            long nearest = searcher.SearchBackward(3, '\n', new Progress<int>()).First();
            Assert.Equal(1, nearest);
        }
    }
}
