namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the JSON settings store round-trip (which also exercises the Native-AOT source-generated
    /// serializer) and its resilience to missing / corrupt files.
    /// </summary>
    public class SettingsStoreTests
    {
        private static string TempPath() =>
            Path.Combine(Path.GetTempPath(), "fujiy-settings-" + Guid.NewGuid().ToString("N") + ".json");

        [Fact]
        public void SaveThenLoad_RoundTripsAllFields()
        {
            string path = TempPath();
            try
            {
                var store = new SettingsStore(path);
                var saved = new AppSettings
                {
                    TabWidth = 8,
                    WindowWidth = 1234,
                    WindowHeight = 777,
                    WindowMaximized = true,
                    RecentFiles = { @"C:\a.txt", @"C:\b.log" },
                    RecentSearches = { "ERROR", "Thread-33" },
                    RecentFilters = { "WARN" },
                    RestoreLastSession = false,
                    LastSessionFilePath = @"C:\logs\app.log",
                    LastSessionFirstVisibleLine = 4200,
                    LastSessionCaretLine = 4321,
                    LastSessionCaretColumn = 7,
                    WordWrap = true,
                    HighlightSelectionOccurrences = false,
                };

                store.Save(saved);
                AppSettings loaded = store.Load();

                Assert.Equal(8, loaded.TabWidth);
                Assert.Equal(1234, loaded.WindowWidth);
                Assert.Equal(777, loaded.WindowHeight);
                Assert.True(loaded.WindowMaximized);
                Assert.Equal(new[] { @"C:\a.txt", @"C:\b.log" }, loaded.RecentFiles);
                Assert.Equal(new[] { "ERROR", "Thread-33" }, loaded.RecentSearches);
                Assert.Equal(new[] { "WARN" }, loaded.RecentFilters);
                Assert.False(loaded.RestoreLastSession);
                Assert.Equal(@"C:\logs\app.log", loaded.LastSessionFilePath);
                Assert.Equal(4200, loaded.LastSessionFirstVisibleLine);
                Assert.Equal(4321, loaded.LastSessionCaretLine);
                Assert.Equal(7, loaded.LastSessionCaretColumn);
                Assert.True(loaded.WordWrap);
                Assert.False(loaded.HighlightSelectionOccurrences);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Load_MissingFile_ReturnsDefaults()
        {
            AppSettings s = new SettingsStore(TempPath()).Load();

            Assert.Equal(4, s.TabWidth);
            Assert.Equal(0, s.WindowWidth);
            Assert.False(s.WindowMaximized);
            Assert.Empty(s.RecentFiles);
            Assert.True(s.RestoreLastSession); // session restore is on by default
            Assert.True(s.HighlightSelectionOccurrences); // selection-occurrence highlighting is on by default
            Assert.Equal("", s.LastSessionFilePath);
        }

        [Fact]
        public void Load_CorruptFile_ReturnsDefaults()
        {
            string path = TempPath();
            File.WriteAllText(path, "{ this is not valid json ]");
            try
            {
                Assert.Equal(4, new SettingsStore(path).Load().TabWidth);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
