namespace FujiyNotepad.Presentation
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

        /// <summary>Whether long lines are wrapped to the viewport width (issue #31). Off by default.</summary>
        public bool WordWrap { get; set; }

        /// <summary>Whether selecting text highlights all other occurrences of it in the view (issue #130). On by default.</summary>
        public bool HighlightSelectionOccurrences { get; set; } = true;

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

        /// <summary>Most-recently-used Find terms, newest first (see <c>SearchHistory</c>).</summary>
        public List<string> RecentSearches { get; set; } = new();

        /// <summary>Most-recently-used Filter terms, newest first (see <c>SearchHistory</c>).</summary>
        public List<string> RecentFilters { get; set; } = new();

        /// <summary>Whether to reopen the last file (at its last scroll/caret position) on startup. On by default.</summary>
        public bool RestoreLastSession { get; set; } = true;

        /// <summary>
        /// The file that was open when the app last closed, reopened on startup when <see cref="RestoreLastSession"/>
        /// is on. Empty means "nothing to restore".
        /// </summary>
        public string LastSessionFilePath { get; set; } = "";

        /// <summary>The first visible line (0-based scroll position) of the last file when it was closed.</summary>
        public int LastSessionFirstVisibleLine { get; set; }

        /// <summary>The caret line (0-based) of the last file when it was closed.</summary>
        public int LastSessionCaretLine { get; set; }

        /// <summary>The caret column (0-based) of the last file when it was closed.</summary>
        public int LastSessionCaretColumn { get; set; }
    }
}
