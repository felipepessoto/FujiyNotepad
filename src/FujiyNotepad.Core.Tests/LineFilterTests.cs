using System;
using System.Collections.Generic;
using System.Threading;
using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests
{
    public class LineFilterTests
    {
        private sealed class FakeLines : ILineSource
        {
            private readonly string[] lines;
            public FakeLines(params string[] lines) => this.lines = lines;
            public int LineCount => lines.Length;
            public string GetLine(int lineIndex) => lines[lineIndex];
        }

        private static bool Contains(string line, string term) =>
            line.Contains(term, StringComparison.Ordinal);

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
        public void Match_CapsAtMaxMatchesAndReportsCapped()
        {
            var src = new FakeLines("x", "x", "x", "x", "x");

            List<int> hits = LineFilter.Match(src, l => Contains(l, "x"), out bool capped, maxMatches: 3);

            Assert.Equal(new[] { 0, 1, 2 }, hits);
            Assert.True(capped);
        }

        [Fact]
        public void Match_PreCancelledToken_Throws()
        {
            var src = new FakeLines("a", "b");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(
                () => LineFilter.Match(src, _ => true, out _, token: cts.Token));
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
