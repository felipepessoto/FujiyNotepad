namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the search-history MRU helper: newest-first ordering, case-insensitive de-dup, capping, and
    /// blank-term handling. Mirrors <c>RecentFilesTests</c> but with the search list's larger cap.
    /// </summary>
    public class SearchHistoryTests
    {
        [Fact]
        public void Add_PutsNewestFirst()
        {
            var list = SearchHistory.Add(new[] { "error", "warn" }, "timeout");

            Assert.Equal(new[] { "timeout", "error", "warn" }, list);
        }

        [Fact]
        public void Add_MovesExistingEntryToFront_WithoutDuplicating()
        {
            var list = SearchHistory.Add(new[] { "a", "b", "c" }, "b");

            Assert.Equal(new[] { "b", "a", "c" }, list);
        }

        [Fact]
        public void Add_DeduplicatesCaseInsensitively_KeepingTheNewCasing()
        {
            var list = SearchHistory.Add(new[] { "ERROR" }, "error");

            Assert.Single(list);
            Assert.Equal("error", list[0]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Add_IgnoresBlankTerm_ReturningACopyOfExisting(string? term)
        {
            var existing = new List<string> { "a", "b" };

            var list = SearchHistory.Add(existing, term!);

            Assert.Equal(new[] { "a", "b" }, list);
            Assert.NotSame(existing, list); // a copy, never the same reference
        }

        [Fact]
        public void Add_KeepsTermsThatContainSpaces()
        {
            var list = SearchHistory.Add(System.Array.Empty<string>(), "Thread-33 GET");

            Assert.Equal(new[] { "Thread-33 GET" }, list);
        }

        [Fact]
        public void Add_CapsAtMax_DroppingTheOldest()
        {
            var list = SearchHistory.Add(new[] { "b", "c" }, "a", max: 2);

            Assert.Equal(new[] { "a", "b" }, list);
        }

        [Fact]
        public void Add_DefaultCapIsTwenty()
        {
            List<string> list = new();
            for (int i = 0; i < 25; i++)
            {
                list = SearchHistory.Add(list, $"term{i}");
            }

            Assert.Equal(SearchHistory.MaxCount, list.Count);
            Assert.Equal("term24", list[0]); // newest kept
            Assert.Equal("term5", list[^1]);  // term0..term4 dropped
        }
    }
}
