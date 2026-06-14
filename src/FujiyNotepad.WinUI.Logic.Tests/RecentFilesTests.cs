namespace FujiyNotepad.WinUI.Logic.Tests
{
    /// <summary>Tests the MRU helpers: newest-first ordering, case-insensitive de-dup, capping, pruning.</summary>
    public class RecentFilesTests
    {
        [Fact]
        public void Add_PutsNewestFirst()
        {
            var list = RecentFiles.Add(new[] { "a.txt", "b.txt" }, "c.txt");

            Assert.Equal(new[] { "c.txt", "a.txt", "b.txt" }, list);
        }

        [Fact]
        public void Add_MovesExistingEntryToFront_WithoutDuplicating()
        {
            var list = RecentFiles.Add(new[] { "a.txt", "b.txt", "c.txt" }, "b.txt");

            Assert.Equal(new[] { "b.txt", "a.txt", "c.txt" }, list);
        }

        [Fact]
        public void Add_DeduplicatesCaseInsensitively()
        {
            var list = RecentFiles.Add(new[] { @"C:\Dir\File.TXT" }, @"c:\dir\file.txt");

            Assert.Single(list);
            Assert.Equal(@"c:\dir\file.txt", list[0]);
        }

        [Fact]
        public void Add_CapsAtMax()
        {
            var existing = Enumerable.Range(0, 10).Select(i => $"f{i}.txt").ToList();

            var list = RecentFiles.Add(existing, "new.txt", max: 10);

            Assert.Equal(10, list.Count);
            Assert.Equal("new.txt", list[0]);
            Assert.DoesNotContain("f9.txt", list); // the oldest fell off the end
        }

        [Fact]
        public void Add_BlankPath_ReturnsExistingUnchanged()
        {
            var existing = new[] { "a.txt" };

            Assert.Equal(existing, RecentFiles.Add(existing, "   "));
        }

        [Fact]
        public void Prune_RemovesEntriesThatDoNotExist()
        {
            var kept = new HashSet<string> { "a.txt", "c.txt" };

            var list = RecentFiles.Prune(new[] { "a.txt", "b.txt", "c.txt" }, kept.Contains);

            Assert.Equal(new[] { "a.txt", "c.txt" }, list);
        }
    }
}
