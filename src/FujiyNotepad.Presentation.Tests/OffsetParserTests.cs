namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>Tests the "Go To Offset" parser: decimal, 0x-prefixed hex, whitespace, and rejection.</summary>
    public class OffsetParserTests
    {
        [Theory]
        [InlineData("0", 0)]
        [InlineData("1024", 1024)]
        [InlineData("  42  ", 42)]
        [InlineData("9223372036854775807", long.MaxValue)]
        public void Parses_Decimal(string text, long expected)
        {
            Assert.True(OffsetParser.TryParse(text, out long offset));
            Assert.Equal(expected, offset);
        }

        [Theory]
        [InlineData("0x10", 16)]
        [InlineData("0X1F", 31)]
        [InlineData("0xff", 255)]
        [InlineData("  0x100  ", 256)]
        public void Parses_Hex_WithPrefix_CaseInsensitive(string text, long expected)
        {
            Assert.True(OffsetParser.TryParse(text, out long offset));
            Assert.Equal(expected, offset);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("abc")]
        [InlineData("12.5")]
        [InlineData("0xZZ")]
        [InlineData("0x")]
        [InlineData("ff")] // hex without the 0x prefix is not accepted
        public void Rejects_Invalid(string? text)
        {
            Assert.False(OffsetParser.TryParse(text, out long offset));
            Assert.Equal(0, offset);
        }
    }
}
