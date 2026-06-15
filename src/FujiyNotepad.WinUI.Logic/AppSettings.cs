namespace FujiyNotepad.WinUI.Logic
{
    /// <summary>
    /// User settings persisted across launches (window size, tab width, recently opened files).
    /// Plain data with no UI dependency, so it serializes cleanly and unit-tests on a normal host.
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>Tab width in columns (2, 4, or 8). Defaults to 4.</summary>
        public int TabWidth { get; set; } = 4;

        /// <summary>Last window width in physical pixels; 0 means "unset, use the default size".</summary>
        public int WindowWidth { get; set; }

        /// <summary>Last window height in physical pixels; 0 means "unset, use the default size".</summary>
        public int WindowHeight { get; set; }

        /// <summary>Whether the window was maximized when last closed.</summary>
        public bool WindowMaximized { get; set; }

        /// <summary>Find option: match case exactly. Off (default) folds ASCII case, matching most editors.</summary>
        public bool FindMatchCase { get; set; }

        /// <summary>Find option: match whole words only.</summary>
        public bool FindWholeWord { get; set; }

        /// <summary>Find option: treat the search term as a regular expression (matched per line).</summary>
        public bool FindUseRegex { get; set; }

        /// <summary>Most-recently-opened file paths, newest first.</summary>
        public List<string> RecentFiles { get; set; } = new();
    }
}
