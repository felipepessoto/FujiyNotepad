namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the "Go To Percentage" parser: decimal / percent-suffixed input, bounds and rejection, plus the
    /// percentage-to-byte-offset mapping (rounding and clamping to the last byte).
    /// </summary>
    public class PercentParserTests
    {
        [Theory]
        [InlineData("0", 0)]
        [InlineData("50", 50)]
        [InlineData("100", 100)]
        [InlineData("33.3", 33.3)]
        [InlineData("  75  ", 75)]
        [InlineData("50%", 50)]
        [InlineData("12.5 %", 12.5)]
        [InlineData("+25", 25)]
        public void TryParse_AcceptsValidPercentages(string text, double expected)
        {
            Assert.True(PercentParser.TryParse(text, out double percent));
            Assert.Equal(expected, percent, 3);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("abc")]
        [InlineData("-5")]
        [InlineData("101")]
        [InlineData("100.1")]
        [InlineData("%")]
        [InlineData("0x10")]
        public void TryParse_RejectsInvalidOrOutOfRange(string? text)
        {
            Assert.False(PercentParser.TryParse(text, out double percent));
            Assert.Equal(0, percent);
        }

        [Theory]
        [InlineData(0, 1000, 0)]
        [InlineData(50, 1000, 500)]
        [InlineData(100, 1000, 999)]   // clamped to the last byte, never == length
        [InlineData(33.3, 1000, 333)]
        [InlineData(25, 7, 2)]          // round(1.75) -> 2
        public void ToOffset_MapsAndClamps(double percent, long length, long expected)
        {
            Assert.Equal(expected, PercentParser.ToOffset(percent, length));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ToOffset_NonPositiveLength_IsZero(long length)
        {
            Assert.Equal(0, PercentParser.ToOffset(50, length));
        }

        [Fact]
        public void ToOffset_SingleByteFile_AlwaysZero()
        {
            Assert.Equal(0, PercentParser.ToOffset(0, 1));
            Assert.Equal(0, PercentParser.ToOffset(100, 1));
        }
    }
}
