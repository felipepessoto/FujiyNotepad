namespace FujiyNotepad.WinUI.Logic.Tests
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
                };

                store.Save(saved);
                AppSettings loaded = store.Load();

                Assert.Equal(8, loaded.TabWidth);
                Assert.Equal(1234, loaded.WindowWidth);
                Assert.Equal(777, loaded.WindowHeight);
                Assert.True(loaded.WindowMaximized);
                Assert.Equal(new[] { @"C:\a.txt", @"C:\b.log" }, loaded.RecentFiles);
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
