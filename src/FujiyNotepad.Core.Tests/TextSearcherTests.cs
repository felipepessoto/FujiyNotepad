using System.Collections.Generic;
using System.Threading.Tasks;
using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests
{
    public class TextSearcherTests
    {
        private static async Task<List<long>> SearchAll(IByteSource source, string pattern, int chunkSize = 1 << 20)
        {
            var searcher = new TextSearcher(source, chunkSize);
            var results = new List<long>();
            await foreach (long offset in searcher.Search(0, System.Text.Encoding.ASCII.GetBytes(pattern)))
            {
                results.Add(offset);
            }
            return results;
        }

        [Fact]
        public async Task Search_FindsAllOccurrences()
        {
            var source = new InMemoryByteSource("abcabcabc");
            Assert.Equal(new long[] { 0, 3, 6 }, await SearchAll(source, "abc"));
        }

        [Fact]
        public async Task Search_FindsNewlines()
        {
            var source = new InMemoryByteSource("a\nbb\nccc\n");
            Assert.Equal(new long[] { 1, 4, 8 }, await SearchAll(source, "\n"));
        }

        [Fact]
        public async Task Search_MatchAcrossChunkBoundary_IsFound()
        {
            // Pattern straddles the boundary when chunkSize splits "needle" in half.
            var source = new InMemoryByteSource("xxxxneedleyyyy");
            Assert.Equal(new long[] { 4 }, await SearchAll(source, "needle", chunkSize: 6));
        }

        [Fact]
        public async Task Search_MultipleChunks_FindsAcrossWholeStream()
        {
            var source = new InMemoryByteSource("a.b.c.d.e.f.g.h");
            Assert.Equal(new long[] { 1, 3, 5, 7, 9, 11, 13 }, await SearchAll(source, ".", chunkSize: 4));
        }

        [Fact]
        public async Task Search_StartOffsetBeyondEnd_YieldsNothing()
        {
            var source = new InMemoryByteSource("abc");
            var searcher = new TextSearcher(source);
            var results = new List<long>();
            await foreach (long offset in searcher.Search(100, new byte[] { (byte)'a' }))
            {
                results.Add(offset);
            }
            Assert.Empty(results);
        }

        [Fact]
        public void SearchBackward_YieldsDescendingThenMinusOneForNewline()
        {
            var source = new InMemoryByteSource("a\nb\nc");
            var searcher = new TextSearcher(source);
            Assert.Equal(new long[] { 3, 1, -1 }, new List<long>(searcher.SearchBackward(5, (byte)'\n')));
        }

        [Fact]
        public void SearchBackward_NonNewline_NoMinusOneSentinel()
        {
            var source = new InMemoryByteSource("axbxc");
            var searcher = new TextSearcher(source);
            Assert.Equal(new long[] { 3, 1 }, new List<long>(searcher.SearchBackward(5, (byte)'x')));
        }
    }
}
