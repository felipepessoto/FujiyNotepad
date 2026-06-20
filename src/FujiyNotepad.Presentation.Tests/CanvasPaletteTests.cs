namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the text-surface palette selection (issue #76): normal use returns the curated light/dark
    /// palette unchanged, and Windows High Contrast maps the system colours onto every role and always wins.
    /// </summary>
    public class CanvasPaletteTests
    {
        // Distinct sentinel values so every High-Contrast role-to-source mapping is verifiable.
        private static readonly HighContrastColors Hc = new(
            Window: 0xFF110000u,
            WindowText: 0xFF220000u,
            Highlight: 0xFF330000u,
            GrayText: 0xFF440000u,
            Hotlight: 0xFF550000u);

        [Fact]
        public void Light_WhenNotDarkAndNotHighContrast()
        {
            CanvasColors c = CanvasPalette.Resolve(isDark: false, isHighContrast: false, Hc);

            Assert.Equal(CanvasPalette.Light, c);
        }

        [Fact]
        public void Dark_WhenDarkAndNotHighContrast()
        {
            CanvasColors c = CanvasPalette.Resolve(isDark: true, isHighContrast: false, Hc);

            Assert.Equal(CanvasPalette.Dark, c);
        }

        [Fact]
        public void HighContrast_MapsEveryRoleToSystemColors()
        {
            CanvasColors c = CanvasPalette.Resolve(isDark: false, isHighContrast: true, Hc);

            Assert.Equal(Hc.Window, c.Background);
            Assert.Equal(Hc.WindowText, c.Text);
            Assert.Equal(Hc.WindowText, c.Caret);
            Assert.Equal(Hc.Highlight, c.Selection);
            Assert.Equal(Hc.Hotlight, c.MatchHighlight);
            Assert.Equal(Hc.Hotlight, c.SelectionOccurrence);
            Assert.Equal(Hc.GrayText, c.GutterText);
            Assert.Equal(Hc.GrayText, c.GutterSeparator);
            Assert.Equal(Hc.Hotlight, c.Bookmark);
            Assert.Equal(Hc.GrayText, c.Whitespace);
            Assert.Equal(Hc.Hotlight, c.TrailingWhitespace);
            Assert.Equal(Hc.Hotlight, c.ControlChar);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void HighContrast_IgnoresIsDark(bool isDark)
        {
            CanvasColors c = CanvasPalette.Resolve(isDark, isHighContrast: true, Hc);

            // Same High-Contrast result regardless of the resolved light/dark theme.
            Assert.Equal(Hc.Window, c.Background);
            Assert.Equal(Hc.WindowText, c.Text);
        }

        [Fact]
        public void HighContrast_OverridesTheCuratedDarkPalette()
        {
            CanvasColors c = CanvasPalette.Resolve(isDark: true, isHighContrast: true, Hc);

            Assert.NotEqual(CanvasPalette.Dark, c);
            Assert.Equal(Hc.Window, c.Background);
        }
    }
}
