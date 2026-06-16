namespace FujiyNotepad.WinUI.Logic.Tests
{
    /// <summary>Tests the "Copy with Line Numbers" formatting (1-based numbers, right-aligned, CRLF-joined).</summary>
    public class LineNumberedCopyTests
    {
        [Fact]
        public void Empty_ReturnsEmptyString()
        {
            Assert.Equal(string.Empty, LineNumberedCopy.Format(1, System.Array.Empty<string>()));
        }

        [Fact]
        public void SingleLine_PrefixesNumber()
        {
            Assert.Equal("5: hello", LineNumberedCopy.Format(5, new[] { "hello" }));
        }

        [Fact]
        public void MultipleLines_JoinedWithCrLf()
        {
            string result = LineNumberedCopy.Format(1, new[] { "a", "b", "c" });

            Assert.Equal("1: a\r\n2: b\r\n3: c", result);
        }

        [Fact]
        public void Numbers_AreRightAlignedToWidestInRange()
        {
            // Range 9..11 -> widest is "11" (2 digits), so 9 is padded to " 9".
            string result = LineNumberedCopy.Format(9, new[] { "x", "y", "z" });

            Assert.Equal(" 9: x\r\n10: y\r\n11: z", result);
        }

        [Fact]
        public void PreservesEmptyLines()
        {
            string result = LineNumberedCopy.Format(1, new[] { "first", "", "third" });

            Assert.Equal("1: first\r\n2: \r\n3: third", result);
        }
    }
}
