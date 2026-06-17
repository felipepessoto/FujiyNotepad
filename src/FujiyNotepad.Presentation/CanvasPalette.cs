namespace FujiyNotepad.Presentation
{
    /// <summary>The live Windows High-Contrast system colours, packed 0xAARRGGBB. The platform layer reads
    /// these (via <c>GetSysColor</c>) and passes them in so <see cref="CanvasPalette"/> stays device-free.</summary>
    public readonly record struct HighContrastColors(
        uint Window,
        uint WindowText,
        uint Highlight,
        uint GrayText,
        uint Hotlight);

    /// <summary>The full set of colours the Win2D text surface paints with (each packed 0xAARRGGBB).</summary>
    public readonly record struct CanvasColors(
        uint Background,
        uint Text,
        uint Caret,
        uint Selection,
        uint MatchHighlight,
        uint GutterText,
        uint GutterSeparator,
        uint Bookmark,
        uint Whitespace,
        uint TrailingWhitespace,
        uint ControlChar);

    /// <summary>
    /// Selects the text-surface palette. In normal use it returns a curated light or dark editor palette
    /// chosen from the control's resolved theme. When Windows High Contrast is on it instead maps the user's
    /// system High-Contrast colours onto the surface, and that always wins: Windows forces the rest of the UI
    /// (menus, status bar) to its high-contrast palette regardless of any Light/Dark choice, so the custom-drawn
    /// canvas must match it rather than show its own colours. Free of any UI dependency, so the selection logic
    /// unit-tests without a graphics device (issue #76).
    /// </summary>
    public static class CanvasPalette
    {
        // Curated light palette. Most entries are opaque; the whitespace/marker alphas read as tints because
        // the opaque line text is always drawn on top.
        private static readonly CanvasColors LightPalette = new(
            Background: 0xFFFFFFFFu,
            Text: 0xFF000000u,
            Caret: 0xFF000000u,
            Selection: 0xFFADD6FFu,
            MatchHighlight: 0xFFFFD54Fu,
            GutterText: 0xFF888888u,
            GutterSeparator: 0xFFE0E0E0u,
            Bookmark: 0xFF1A7FD6u,
            Whitespace: 0xD25A5A5Au,
            TrailingWhitespace: 0xEBD03030u,
            ControlChar: 0xEBD03030u);

        // Curated dark palette.
        private static readonly CanvasColors DarkPalette = new(
            Background: 0xFF1E1E1Eu,
            Text: 0xFFF1F1F1u,
            Caret: 0xFFF1F1F1u,
            Selection: 0xFF264F78u,
            MatchHighlight: 0xFF665118u,
            GutterText: 0xFF858585u,
            GutterSeparator: 0xFF333333u,
            Bookmark: 0xFF4FA3E3u,
            Whitespace: 0xCDC8C8C8u,
            TrailingWhitespace: 0xEBF06A6Au,
            ControlChar: 0xEBF06A6Au);

        /// <summary>The curated light palette (exposed for tests).</summary>
        public static CanvasColors Light => LightPalette;

        /// <summary>The curated dark palette (exposed for tests).</summary>
        public static CanvasColors Dark => DarkPalette;

        /// <summary>
        /// Resolves the surface palette. When <paramref name="isHighContrast"/> is set, maps the High-Contrast
        /// system colours (ignoring <paramref name="isDark"/>); otherwise returns the curated dark or light
        /// palette per <paramref name="isDark"/>.
        /// </summary>
        public static CanvasColors Resolve(bool isDark, bool isHighContrast, HighContrastColors highContrast)
        {
            if (isHighContrast)
            {
                return new CanvasColors(
                    Background: highContrast.Window,
                    Text: highContrast.WindowText,
                    Caret: highContrast.WindowText,
                    Selection: highContrast.Highlight,
                    MatchHighlight: highContrast.Hotlight,
                    GutterText: highContrast.GrayText,
                    GutterSeparator: highContrast.GrayText,
                    Bookmark: highContrast.Hotlight,
                    Whitespace: highContrast.GrayText,
                    TrailingWhitespace: highContrast.Hotlight,
                    ControlChar: highContrast.Hotlight);
            }

            return isDark ? DarkPalette : LightPalette;
        }
    }
}
