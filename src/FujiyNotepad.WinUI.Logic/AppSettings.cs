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

        /// <summary>Monospace font family used by the text view. Defaults to Consolas.</summary>
        public string FontFamily { get; set; } = "Consolas";

        /// <summary>Font size in points (drives zoom). Defaults to 14.</summary>
        public double FontSize { get; set; } = 14;

        /// <summary>Whether the line-number gutter is shown. Off by default.</summary>
        public bool ShowLineNumbers { get; set; }

        /// <summary>Whether whitespace/control markers are overlaid on the text. Off by default.</summary>
        public bool ShowWhitespace { get; set; }

        /// <summary>App theme override: "System" (default), "Light", or "Dark".</summary>
        public string Theme { get; set; } = "System";

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

        /// <summary>
        /// The user's persistent highlight rules as editable text (one rule per line, see
        /// <c>HighlightRuleText</c>). Stored verbatim so comments and ordering survive a round-trip.
        /// </summary>
        public string HighlightRulesText { get; set; } = "";

        /// <summary>Most-recently-opened file paths, newest first.</summary>
        public List<string> RecentFiles { get; set; } = new();
    }
}
