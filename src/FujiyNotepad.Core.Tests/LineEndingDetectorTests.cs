using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests
{
    public class LineEndingDetectorTests
    {
        [Fact]
        public void Detect_Lf()
        {
            var source = new InMemoryByteSource("line1\nline2\nline3");
            Assert.Equal(LineEnding.Lf, LineEndingDetector.Detect(source, TextEncoding.Utf8));
        }

        [Fact]
        public void Detect_CrLf()
        {
            var source = new InMemoryByteSource("line1\r\nline2\r\n");
            Assert.Equal(LineEnding.CrLf, LineEndingDetector.Detect(source, TextEncoding.Utf8));
        }

        [Fact]
        public void Detect_Mixed()
        {
            // A CRLF line followed by an LF-only line is "mixed".
            var source = new InMemoryByteSource("line1\r\nline2\nline3\r\n");
            Assert.Equal(LineEnding.Mixed, LineEndingDetector.Detect(source, TextEncoding.Utf8));
        }

        [Fact]
        public void Detect_None_NoNewline()
        {
            var source = new InMemoryByteSource("single line without a terminator");
            Assert.Equal(LineEnding.None, LineEndingDetector.Detect(source, TextEncoding.Utf8));
        }

        [Fact]
        public void Detect_None_EmptyFile()
        {
            var source = new InMemoryByteSource("");
            Assert.Equal(LineEnding.None, LineEndingDetector.Detect(source, TextEncoding.Utf8));
        }

        [Fact]
        public void Detect_Utf16Le_Lf()
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes("a\nb\nc");
            var source = new InMemoryByteSource(bytes);
            Assert.Equal(LineEnding.Lf, LineEndingDetector.Detect(source, TextEncoding.Utf16Le));
        }

        [Fact]
        public void Detect_Utf16Le_CrLf()
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes("a\r\nb\r\n");
            var source = new InMemoryByteSource(bytes);
            Assert.Equal(LineEnding.CrLf, LineEndingDetector.Detect(source, TextEncoding.Utf16Le));
        }

        [Fact]
        public void Detect_Utf16Be_CrLf()
        {
            var bytes = System.Text.Encoding.BigEndianUnicode.GetBytes("x\r\ny\r\n");
            var source = new InMemoryByteSource(bytes);
            Assert.Equal(LineEnding.CrLf, LineEndingDetector.Detect(source, TextEncoding.Utf16Be));
        }

        [Fact]
        public void ToLabel_MapsEachValue()
        {
            Assert.Equal("LF", LineEndingDetector.ToLabel(LineEnding.Lf));
            Assert.Equal("CRLF", LineEndingDetector.ToLabel(LineEnding.CrLf));
            Assert.Equal("Mixed", LineEndingDetector.ToLabel(LineEnding.Mixed));
            Assert.Equal(string.Empty, LineEndingDetector.ToLabel(LineEnding.None));
        }
    }
}
