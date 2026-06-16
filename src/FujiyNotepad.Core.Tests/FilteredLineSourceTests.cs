using System.Collections.Generic;
using FujiyNotepad.Core;
using FujiyNotepad.TestSupport;

namespace FujiyNotepad.Core.Tests
{
    public class FilteredLineSourceTests
    {
        [Fact]
        public void LineCount_IsTheSubsetSize()
        {
            var src = new FakeLines("a", "b", "c", "d");
            var filtered = new FilteredLineSource(src, new[] { 1, 3 });

            Assert.Equal(2, filtered.LineCount);
        }

        [Fact]
        public void GetLine_MapsFilteredRowToSourceLine()
        {
            var src = new FakeLines("zero", "one", "two", "three");
            var filtered = new FilteredLineSource(src, new[] { 0, 2, 3 });

            Assert.Equal("zero", filtered.GetLine(0));
            Assert.Equal("two", filtered.GetLine(1));
            Assert.Equal("three", filtered.GetLine(2));
        }

        [Fact]
        public void SourceLineAt_ReturnsTheUnderlyingZeroBasedLine()
        {
            var src = new FakeLines("a", "b", "c", "d", "e");
            var filtered = new FilteredLineSource(src, new[] { 1, 4 });

            Assert.Equal(1, filtered.SourceLineAt(0));
            Assert.Equal(4, filtered.SourceLineAt(1));
        }

        [Fact]
        public void GetLineEnding_ForwardsFromTheSourceLine()
        {
            var src = new FakeLinesWithEndings(
                new[] { "a", "b", "c", "d" },
                new[] { LineEnding.Lf, LineEnding.CrLf, LineEnding.None, LineEnding.CrLf });
            // Filter to source lines 1 and 2, whose endings are CrLf and None.
            var filtered = new FilteredLineSource(src, new[] { 1, 2 });

            Assert.Equal(LineEnding.CrLf, filtered.GetLineEnding(0));
            Assert.Equal(LineEnding.None, filtered.GetLineEnding(1));
        }

        [Fact]
        public void GetLineEnding_ReturnsNone_WhenSourceHasNoLineEndingCapability()
        {
            var src = new FakeLines("a", "b", "c");
            var filtered = new FilteredLineSource(src, new[] { 0, 2 });

            Assert.Equal(LineEnding.None, filtered.GetLineEnding(0));
            Assert.Equal(LineEnding.None, filtered.GetLineEnding(1));
        }

        [Fact]
        public void EmptySubset_HasNoLines()
        {
            var src = new FakeLines("a", "b", "c");
            var filtered = new FilteredLineSource(src, new List<int>());

            Assert.Equal(0, filtered.LineCount);
        }
    }
}
