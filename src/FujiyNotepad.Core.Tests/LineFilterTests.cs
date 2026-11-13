using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using FujiyNotepad.Core;
using FujiyNotepad.TestSupport;

namespace FujiyNotepad.Core.Tests
{
    public class LineFilterTests
    {
        private static bool Contains(string line, string term) =>
            line.Contains(term, StringComparison.Ordinal);

        private static async Task<LineProvider> BuildProviderAsync(IByteSource source)
        {
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            return new LineProvider(source, indexer);
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(false, 5)]
        [InlineData(false, 8)]
        [InlineData(true, 0)]
        [InlineData(true, 5)]
        [InlineData(true, 8)]
        public async Task Match_ExcludeRegex_TruncationDuringScan_RejectsUnreadRows(bool cacheRows, int remainingBytes)
        {
            using var source = new GrowableByteSource("keep\nERROR\nother\n");
            LineProvider provider = await BuildProviderAsync(source);
            if (cacheRows)
            {
                for (int i = 0; i < provider.LineCount; i++)
                {
                    provider.GetLine(i);
                }
            }
            var regex = new Regex("ERROR", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            int calls = 0;

            Assert.Throws<EndOfStreamException>(() =>
                LineFilter.Match(provider, line =>
                {
                    if (calls++ == 0)
                    {
                        source.Truncate(remainingBytes);
                    }
                    return regex.IsMatch(line);
                }, out _, exclude: true));

            Assert.Equal(cacheRows ? "ERROR" : remainingBytes == 8 ? "ERR" : string.Empty,
                provider.GetLineUncached(1));
        }

        [Fact]
        public async Task Match_ExcludeRegex_MissingSparseRow_IsNotReplacedByAnotherLine()
        {
            using var source = new GrowableByteSource("first\nsecond\nthird");
            LineProvider provider = await BuildProviderAsync(source);
            var filtered = new FilteredLineSource(provider, new[] { 2 });
            source.Truncate(3);
            source.RefreshLength();
            provider.RefreshLength();
            var regex = new Regex("ERROR", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

            Assert.Throws<EndOfStreamException>(() =>
                LineFilter.Match(filtered, line => regex.IsMatch(line), out _, exclude: true));

            Assert.Equal("fir", provider.GetLine(2));
        }

        [Fact]
        public async Task Match_ExcludeRegex_TruncatedLongLine_RejectsMissingTailBeforeKeepingIt()
        {
            using var source = new GrowableByteSource(new string('x', 70_000) + "\nERROR\n");
            LineProvider provider = await BuildProviderAsync(source);
            string displayed = provider.GetLine(0);
            source.Truncate(65_540);
            var regex = new Regex("ERROR", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

            Assert.Throws<EndOfStreamException>(() =>
                LineFilter.Match(provider, line => regex.IsMatch(line), out _, maxMatches: 1, exclude: true));

            Assert.Equal(displayed, provider.GetLine(0));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Match_ExcludeRegex_PreservesEmptyLinesAndMatchingOptions(bool matchCase)
        {
            using var source = new GrowableByteSource("\n\r\nerror\nERROR\nERRORS\nend");
            LineProvider provider = await BuildProviderAsync(source);
            for (int i = 0; i < provider.LineCount; i++)
            {
                provider.GetLine(i);
            }
            RegexOptions options = RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            var regex = new Regex(@"\bERROR\b", options, TimeSpan.FromMilliseconds(250));

            var included = LineFilter.Match(provider, line => regex.IsMatch(line), out _);
            var excluded = LineFilter.Match(provider, line => regex.IsMatch(line), out bool capped, exclude: true);

            Assert.Equal(matchCase ? new[] { 3 } : new[] { 2, 3 }, included);
            Assert.Equal(matchCase ? new[] { 0, 1, 2, 4, 5 } : new[] { 0, 1, 4, 5 }, excluded);
            Assert.False(capped);
        }

        [Fact]
        public async Task Match_ExcludeRegex_PreservesTheDecodeCap()
        {
            using var source = new GrowableByteSource("keep\n" + new string('x', 70_000) + "ERROR");
            LineProvider provider = await BuildProviderAsync(source);
            string displayed = provider.GetLine(1);
            var regex = new Regex("ERROR", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            var scanned = new List<string>();

            var lines = LineFilter.Match(provider, line =>
            {
                scanned.Add(line);
                return regex.IsMatch(line);
            }, out bool capped, exclude: true);

            Assert.Equal(new[] { 0, 1 }, lines);
            Assert.False(capped);
            Assert.Equal(displayed, scanned[1]);
            Assert.Equal(64 * 1024 + 2, scanned[1].Length);
            Assert.EndsWith(" \u2026", scanned[1]);
        }

        [Fact]
        public async Task Match_ExcludeRegex_KeptCap_DoesNotReadPastTheConfirmedPrefix()
        {
            using var source = new GrowableByteSource("keep\nERROR\nother\n");
            LineProvider provider = await BuildProviderAsync(source);
            var regex = new Regex("ERROR", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

            var lines = LineFilter.Match(provider, line =>
            {
                source.Truncate(5);
                return regex.IsMatch(line);
            }, out bool capped, maxMatches: 1, exclude: true);

            Assert.Equal(new[] { 0 }, lines);
            Assert.True(capped);
        }

        [Fact]
        public async Task Match_ExcludeRegex_CancelledBetweenCachedRows_StopsBeforeTheNextPredicate()
        {
            using var source = new GrowableByteSource("keep\nERROR\nother\n");
            LineProvider provider = await BuildProviderAsync(source);
            for (int i = 0; i < provider.LineCount; i++)
            {
                provider.GetLine(i);
            }
            var regex = new Regex("ERROR", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            using var cts = new CancellationTokenSource();
            int calls = 0;

            Assert.ThrowsAny<OperationCanceledException>(() =>
                LineFilter.Match(provider, line =>
                {
                    calls++;
                    cts.Cancel();
                    return regex.IsMatch(line);
                }, out _, token: cts.Token, exclude: true));

            Assert.Equal(1, calls);
            Assert.Equal("ERROR", provider.GetLine(1));
        }

        [Fact]
        public void Match_ReturnsIndicesOfMatchingLines()
        {
            var src = new FakeLines("INFO start", "ERROR boom", "INFO tick", "ERROR again", "done");

            List<int> hits = LineFilter.Match(src, l => Contains(l, "ERROR"), out bool capped);

            Assert.Equal(new[] { 1, 3 }, hits);
            Assert.False(capped);
        }

        [Fact]
        public void Match_NoMatches_ReturnsEmpty()
        {
            var src = new FakeLines("a", "b", "c");
            Assert.Empty(LineFilter.Match(src, l => Contains(l, "zzz"), out _));
        }

        [Fact]
        public void Match_Exclude_ReturnsTheComplement()
        {
            var src = new FakeLines("INFO start", "ERROR boom", "INFO tick", "ERROR again", "done");

            List<int> hits = LineFilter.Match(src, l => Contains(l, "ERROR"), out bool capped, exclude: true);

            Assert.Equal(new[] { 0, 2, 4 }, hits);
            Assert.False(capped);
        }

        [Theory]
        [InlineData("x", 0)]
        [InlineData("absent", 3)]
        public void Match_Exclude_AllOrNoMatches(string term, int expectedCount)
        {
            var src = new FakeLines("x", "x", "x");

            List<int> hits = LineFilter.Match(src, l => Contains(l, term), out bool capped, exclude: true);

            Assert.Equal(Enumerable.Range(0, expectedCount), hits);
            Assert.False(capped);
        }

        [Fact]
        public void Match_CapsAtMaxMatchesAndReportsCapped()
        {
            var src = new FakeLines("x", "x", "x", "x", "x");

            List<int> hits = LineFilter.Match(src, l => Contains(l, "x"), out bool capped, maxMatches: 3);

            Assert.Equal(new[] { 0, 1, 2 }, hits);
            Assert.True(capped);
        }

        [Fact]
        public void Match_Exclude_CapsKeptLinesNotDiscardedMatches()
        {
            var src = new FakeLines("x", "x", "x", "keep", "x", "keep", "keep");

            List<int> hits = LineFilter.Match(src, l => Contains(l, "x"), out bool capped, maxMatches: 2, exclude: true);

            Assert.Equal(new[] { 3, 5 }, hits);
            Assert.True(capped);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Match_PreCancelledToken_Throws(bool exclude)
        {
            var src = new FakeLines("a", "b");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(
                () => LineFilter.Match(src, _ => true, out _, token: cts.Token, exclude: exclude));
        }

        [Fact]
        public void FilteredLineSource_ExposesOnlyMatchingLines()
        {
            var src = new FakeLines("zero", "one", "two", "three");
            var filtered = new FilteredLineSource(src, new[] { 1, 3 });

            Assert.Equal(2, filtered.LineCount);
            Assert.Equal("one", filtered.GetLine(0));
            Assert.Equal("three", filtered.GetLine(1));
            Assert.Equal(1, filtered.SourceLineAt(0));
            Assert.Equal(3, filtered.SourceLineAt(1));
        }

        [Fact]
        public void FilteredLineSource_EmptySubset_HasNoLines()
        {
            var filtered = new FilteredLineSource(new FakeLines("a", "b"), Array.Empty<int>());
            Assert.Equal(0, filtered.LineCount);
        }
    }
}
