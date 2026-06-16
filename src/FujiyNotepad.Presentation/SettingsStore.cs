using System.Text.Json;
using System.Text.Json.Serialization;

namespace FujiyNotepad.Presentation
{
    // Source-generated JSON metadata so (de)serialization works under Native AOT without reflection.
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(AppSettings))]
    internal sealed partial class SettingsJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// Loads and saves <see cref="AppSettings"/> as a JSON file. All I/O is best-effort: a missing,
    /// corrupt, or locked file yields defaults, and save failures are swallowed, so settings never crash
    /// the app or block startup/shutdown.
    /// </summary>
    public sealed class SettingsStore
    {
        private readonly string filePath;

        public SettingsStore(string filePath) => this.filePath = filePath;

        /// <summary>The default store at <c>%LOCALAPPDATA%\FujiyNotepad\settings.json</c>.</summary>
        public static SettingsStore Default()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FujiyNotepad");
            return new SettingsStore(Path.Combine(dir, "settings.json"));
        }

        public AppSettings Load()
        {
            try
            {
                if (File.Exists(filePath))
                {
                    AppSettings? loaded = JsonSerializer.Deserialize(
                        File.ReadAllText(filePath), SettingsJsonContext.Default.AppSettings);
                    if (loaded is not null)
                    {
                        loaded.RecentFiles ??= new List<string>();
                        return loaded;
                    }
                }
            }
            catch
            {
                // Ignore: a missing/corrupt/locked file just means we start from defaults.
            }

            return new AppSettings();
        }

        public void Save(AppSettings settings)
        {
            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(filePath,
                    JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
            }
            catch
            {
                // Best-effort: never let a settings write break the app.
            }
        }
    }
}
