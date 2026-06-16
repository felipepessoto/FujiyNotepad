namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>Tests colour-token resolution for highlight rules (names + #RGB / #RRGGBB / #AARRGGBB).</summary>
    public class HighlightColorsTests
    {
        [Fact]
        public void NamedColor_GetsDefaultAlpha()
        {
            Assert.True(HighlightColors.TryParse("red", out uint argb));
            Assert.Equal(HighlightColors.DefaultAlpha | 0xFF5252u, argb);
        }

        [Fact]
        public void NamedColor_IsCaseInsensitive()
        {
            Assert.True(HighlightColors.TryParse("Blue", out uint a));
            Assert.True(HighlightColors.TryParse("BLUE", out uint b));
            Assert.Equal(a, b);
        }

        [Fact]
        public void Hex6_GetsDefaultAlpha()
        {
            Assert.True(HighlightColors.TryParse("#FF0000", out uint argb));
            Assert.Equal(0x80FF0000u, argb);
        }

        [Fact]
        public void Hex3_IsExpanded()
        {
            Assert.True(HighlightColors.TryParse("#F00", out uint argb));
            Assert.Equal(0x80FF0000u, argb);
        }

        [Fact]
        public void Hex8_KeepsExplicitAlpha()
        {
            Assert.True(HighlightColors.TryParse("#FF00FF00", out uint argb));
            Assert.Equal(0xFF00FF00u, argb);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("notacolor")]
        [InlineData("#12")]
        [InlineData("#GGGGGG")]
        public void Invalid_ReturnsFalse(string? token)
        {
            Assert.False(HighlightColors.TryParse(token, out uint argb));
            Assert.Equal(0u, argb);
        }

        [Fact]
        public void DefaultColorName_IsParseable()
        {
            Assert.True(HighlightColors.TryParse(HighlightColors.DefaultColorName, out _));
        }
    }
}
