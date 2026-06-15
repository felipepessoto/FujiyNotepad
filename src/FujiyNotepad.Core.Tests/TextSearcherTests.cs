using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests
{
    public class TextSearcherTests
    {
        private sealed class SyncProgress : IProgress<int>
        {
            public readonly List<int> Values = new();

            // Synchronous so tests observe every report deterministically (unlike Progress<T>, which posts).
            public void Report(int value) => Values.Add(value);
        }

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

        [Fact]
        public async Task Search_ReportsProgress_FromZeroToHundredMonotonically()
        {
            // No match, so the search scans the whole source and reports progress to completion.
            var source = new InMemoryByteSource(new string('.', 100));
            var searcher = new TextSearcher(source, chunkSize: 10);
            var progress = new SyncProgress();

            await foreach (long _ in searcher.Search(0, System.Text.Encoding.ASCII.GetBytes("zzz"), progress))
            {
            }

            Assert.Equal(0, progress.Values.First());
            Assert.Equal(100, progress.Values.Last());
            Assert.Equal(progress.Values.OrderBy(v => v), progress.Values); // non-decreasing
        }

        [Fact]
        public async Task Search_PreCancelledToken_YieldsNothing()
        {
            var source = new InMemoryByteSource("abcabcabc");
            var searcher = new TextSearcher(source);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var results = new List<long>();
            await foreach (long m in searcher.Search(0, System.Text.Encoding.ASCII.GetBytes("abc"), token: cts.Token))
            {
                results.Add(m);
            }

            Assert.Empty(results);
        }

        [Fact]
        public async Task Search_CancelDuringEnumeration_StopsEarlyWithoutThrowing()
        {
            // "abab..." has 1000 matches; cancelling after the first stops between chunks (cooperatively).
            var source = new InMemoryByteSource(string.Concat(Enumerable.Repeat("ab", 1000)));
            var searcher = new TextSearcher(source, chunkSize: 8);
            using var cts = new CancellationTokenSource();

            var results = new List<long>();
            await foreach (long m in searcher.Search(0, System.Text.Encoding.ASCII.GetBytes("ab"), token: cts.Token))
            {
                results.Add(m);
                cts.Cancel();
            }

            Assert.NotEmpty(results);
            Assert.True(results.Count < 1000, $"expected an early stop, got {results.Count} matches");
        }

        private static async Task<List<long>> SearchAll(IByteSource source, string pattern, SearchOptions options, int chunkSize = 1 << 20)
        {
            var searcher = new TextSearcher(source, chunkSize);
            var results = new List<long>();
            await foreach (long offset in searcher.Search(0, System.Text.Encoding.ASCII.GetBytes(pattern), options))
            {
                results.Add(offset);
            }
            return results;
        }

        [Fact]
        public async Task Search_CaseSensitiveByDefault_SkipsDifferentCase()
        {
            var source = new InMemoryByteSource("ABCabc");
            Assert.Equal(new long[] { 3 }, await SearchAll(source, "abc"));
        }

        [Fact]
        public async Task Search_IgnoreCase_MatchesEitherCase()
        {
            var source = new InMemoryByteSource("ABCabcAbC");
            Assert.Equal(new long[] { 0, 3, 6 }, await SearchAll(source, "abc", new SearchOptions { IgnoreCase = true }));
        }

        [Fact]
        public async Task Search_IgnoreCase_AcrossChunkBoundary_IsFound()
        {
            var source = new InMemoryByteSource("xxxxNeEdLeyyyy");
            Assert.Equal(new long[] { 4 }, await SearchAll(source, "needle", new SearchOptions { IgnoreCase = true }, chunkSize: 6));
        }

        [Fact]
        public async Task Search_WholeWord_ExcludesMatchesInsideWords()
        {
            // "cat" stands alone at 0 and 12; the ones inside "catalog" (4) and "scatter" (17) are excluded.
            var source = new InMemoryByteSource("cat catalog cat scatter");
            Assert.Equal(new long[] { 0, 12 }, await SearchAll(source, "cat", new SearchOptions { WholeWord = true }));
        }

        [Fact]
        public async Task Search_WholeWord_TreatsDigitsAndUnderscoreAsWordChars()
        {
            // Only the trailing standalone "cat" qualifies; '_' and digits are word characters.
            var source = new InMemoryByteSource("cat_cat cat1 cat");
            Assert.Equal(new long[] { 13 }, await SearchAll(source, "cat", new SearchOptions { WholeWord = true }));
        }

        [Fact]
        public async Task Search_WholeWord_AcrossChunkBoundary_ChecksNeighbourBytes()
        {
            // The match at 4 straddles the chunk split; its right neighbour ('s') must still exclude it,
            // while the standalone match at 11 qualifies.
            var source = new InMemoryByteSource("xxx cats cat");
            Assert.Equal(new long[] { 9 }, await SearchAll(source, "cat", new SearchOptions { WholeWord = true }, chunkSize: 6));
        }

        [Fact]
        public async Task Search_IgnoreCaseAndWholeWord_Combined()
        {
            var source = new InMemoryByteSource("Cat CAT cats");
            Assert.Equal(new long[] { 0, 4 }, await SearchAll(source, "cat", new SearchOptions { IgnoreCase = true, WholeWord = true }));
        }

        [Fact]
        public async Task Search_SelfOverlappingPattern_YieldsNonOverlappingMatches()
        {
            // "xxx" in "xxxxxx" must match at 0 and 3 (not 0/1/2/3), like a text editor's find.
            var source = new InMemoryByteSource("xxxxxx");
            Assert.Equal(new long[] { 0, 3 }, await SearchAll(source, "xxx"));
        }

        [Fact]
        public async Task Search_SelfOverlappingPattern_NonOverlapping_AcrossChunkBoundary()
        {
            // chunkSize 5 splits the run of 9; the cross-chunk carry must not re-yield an overlapping match.
            var source = new InMemoryByteSource("xxxxxxxxx");
            Assert.Equal(new long[] { 0, 3, 6 }, await SearchAll(source, "xxx", chunkSize: 5));
        }

        private static long? FindLast(IByteSource source, string pattern, long beforeOffset, SearchOptions options = default, int chunkSize = 1 << 20)
        {
            var searcher = new TextSearcher(source, chunkSize);
            return searcher.FindLastBefore(beforeOffset, System.Text.Encoding.ASCII.GetBytes(pattern), options);
        }

        [Fact]
        public void FindLastBefore_ReturnsHighestMatchStrictlyBeforeOffset()
        {
            // "ab" occurs at 0, 4, 8; the last start before offset 8 is 4 (8 itself is not "before").
            var source = new InMemoryByteSource("ab..ab..ab");
            Assert.Equal(4, FindLast(source, "ab", beforeOffset: 8));
        }

        [Fact]
        public void FindLastBefore_OffsetPastEnd_ReturnsLastMatch()
        {
            // A large beforeOffset (e.g. wrap-around) yields the final occurrence in the file.
            var source = new InMemoryByteSource("ab..ab..ab");
            Assert.Equal(8, FindLast(source, "ab", beforeOffset: long.MaxValue));
        }

        [Fact]
        public void FindLastBefore_NoMatchBeforeOffset_ReturnsNull()
        {
            var source = new InMemoryByteSource("....ab..ab");
            Assert.Null(FindLast(source, "ab", beforeOffset: 4));
        }

        [Fact]
        public void FindLastBefore_PatternLongerThanFile_ReturnsNull()
        {
            var source = new InMemoryByteSource("ab");
            Assert.Null(FindLast(source, "abc", beforeOffset: long.MaxValue));
        }

        [Fact]
        public void FindLastBefore_MatchStraddlingChunkBoundary_IsFound()
        {
            // chunkSize 6 splits "needle"; the backward block scan's overlap must still complete the match.
            var source = new InMemoryByteSource("xxxxneedleyyyy");
            Assert.Equal(4, FindLast(source, "needle", beforeOffset: long.MaxValue, chunkSize: 6));
        }

        [Fact]
        public void FindLastBefore_LastMatchSpansMultipleChunks_ScansBackwardUntilFound()
        {
            // Only one match, in the first chunk; the scan must walk back through several empty chunks.
            var source = new InMemoryByteSource("needle" + new string('.', 30));
            Assert.Equal(0, FindLast(source, "needle", beforeOffset: long.MaxValue, chunkSize: 4));
        }

        [Fact]
        public void FindLastBefore_IgnoreCase_MatchesEitherCase()
        {
            var source = new InMemoryByteSource("abcABCabc");
            Assert.Equal(6, FindLast(source, "ABC", beforeOffset: long.MaxValue, options: new SearchOptions { IgnoreCase = true }));
        }

        [Fact]
        public void FindLastBefore_WholeWord_SkipsMatchesInsideWords()
        {
            // Standalone "cat" at 0 and 12; the ones inside "catalog" (4) and "scatter" (17) are excluded.
            var source = new InMemoryByteSource("cat catalog cat scatter");
            Assert.Equal(12, FindLast(source, "cat", beforeOffset: long.MaxValue, options: new SearchOptions { WholeWord = true }));
        }

        [Fact]
        public void FindLastBefore_WholeWord_AcrossChunkBoundary_ChecksNeighbourBytes()
        {
            // "cat" at 4 (in "cats") straddles the chunk split and must be excluded; the standalone "cat" at 9 wins.
            var source = new InMemoryByteSource("xxx cats cat");
            Assert.Equal(9, FindLast(source, "cat", beforeOffset: long.MaxValue, options: new SearchOptions { WholeWord = true }, chunkSize: 6));
        }

        [Fact]
        public void FindLastBefore_SelfOverlapping_ReturnsNearestNonOverlappingBeforeOffset()
        {
            // "xxx" in "xxxxxx": from end (offset 6) the previous match is 3; the canonical forward set is {0,3}.
            var source = new InMemoryByteSource("xxxxxx");
            Assert.Equal(3, FindLast(source, "xxx", beforeOffset: 6));
        }

        [Fact]
        public async Task Search_UnitAlignment_KeepsOnlyAlignedMatches()
        {
            // 'x' sits at offsets 0, 2, 4 (all multiples of 2) in "xAxAxA"; alignment 2 keeps them all.
            Assert.Equal(new long[] { 0, 2, 4 }, await SearchAll(new InMemoryByteSource("xAxAxA"), "x", new SearchOptions { UnitAlignment = 2 }));
        }

        [Fact]
        public async Task Search_UnitAlignment_SkipsMisalignedMatches()
        {
            // 'x' sits at offsets 1, 3, 5 (all odd) in "AxAxAx"; alignment 2 rejects every one.
            Assert.Empty(await SearchAll(new InMemoryByteSource("AxAxAx"), "x", new SearchOptions { UnitAlignment = 2 }));
        }

        [Fact]
        public void FindLastBefore_UnitAlignment_ReturnsHighestAlignedMatch()
        {
            // 'x' at 0, 2, 5; alignment 2 rejects the odd match at 5, so the previous aligned match is 2.
            var searcher = new TextSearcher(new InMemoryByteSource("xAxAAx"));
            Assert.Equal(2, searcher.FindLastBefore(long.MaxValue, System.Text.Encoding.ASCII.GetBytes("x"), new SearchOptions { UnitAlignment = 2 }));
        }
    }
}
