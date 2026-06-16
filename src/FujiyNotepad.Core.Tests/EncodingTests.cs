using System.Text;
using FujiyNotepad.Core;
using FujiyNotepad.TestSupport;

namespace FujiyNotepad.Core.Tests
{
    public class EncodingTests
    {
        // ----- Windows-1252 codec -----

        [Fact]
        public void Windows1252_AsciiPassesThrough()
        {
            Assert.Equal("ABC", Windows1252Encoding.Instance.GetString(new byte[] { 0x41, 0x42, 0x43 }));
        }

        [Fact]
        public void Windows1252_Latin1RangePassesThrough()
        {
            Assert.Equal("é", Windows1252Encoding.Instance.GetString(new byte[] { 0xE9 }));
        }

        [Fact]
        public void Windows1252_PunctuationRangeMapsToUnicode()
        {
            // euro, smart single/double quotes, en/em dash.
            Assert.Equal("€‘’“”–—", Windows1252Encoding.Instance.GetString(
                new byte[] { 0x80, 0x91, 0x92, 0x93, 0x94, 0x96, 0x97 }));
        }

        [Fact]
        public void Windows1252_RoundTripsEncodableChars()
        {
            Assert.Equal(new byte[] { 0x80, 0x97 }, Windows1252Encoding.Instance.GetBytes("€—"));
        }

        [Fact]
        public void Windows1252_UnmappableCharBecomesQuestionMark()
        {
            Assert.Equal(new byte[] { (byte)'?' }, Windows1252Encoding.Instance.GetBytes("\u4E2D")); // CJK
        }

        // ----- TextEncoding facts -----

        [Fact]
        public void TextEncoding_Utf8_IsSingleByteNewline()
        {
            Assert.Equal(new byte[] { 0x0A }, TextEncoding.Utf8.NewLineBytes);
            Assert.Equal(new byte[] { 0x0D }, TextEncoding.Utf8.CarriageReturnBytes);
            Assert.Equal(1, TextEncoding.Utf8.CodeUnitSize);
        }

        [Theory]
        [InlineData("utf-16le", 2)]
        [InlineData("utf-16be", 2)]
        [InlineData("utf-32le", 4)]
        [InlineData("utf-32be", 4)]
        public void TextEncoding_MultiByte_HasExpectedCodeUnitSize(string id, int codeUnit)
        {
            Assert.Equal(codeUnit, TextEncoding.FromId(id).CodeUnitSize);
        }

        [Fact]
        public void TextEncoding_Utf16Le_NewlineAndEncode()
        {
            Assert.Equal(new byte[] { 0x0A, 0x00 }, TextEncoding.Utf16Le.NewLineBytes);
            Assert.Equal(new byte[] { 0x0D, 0x00 }, TextEncoding.Utf16Le.CarriageReturnBytes);
            Assert.Equal(new byte[] { 0x41, 0x00 }, TextEncoding.Utf16Le.Encode("A"));
        }

        [Fact]
        public void TextEncoding_Utf16Be_NewlineIsBigEndian()
        {
            Assert.Equal(new byte[] { 0x00, 0x0A }, TextEncoding.Utf16Be.NewLineBytes);
        }

        [Fact]
        public void TextEncoding_FromId_UnknownOrNull_FallsBackToUtf8()
        {
            Assert.Same(TextEncoding.Utf8, TextEncoding.FromId(null));
            Assert.Same(TextEncoding.Utf8, TextEncoding.FromId("nonsense"));
            Assert.Same(TextEncoding.Utf16Be, TextEncoding.FromId("utf-16be"));
        }

        // ----- Detection: BOM -----

        [Theory]
        [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x41 }, "utf-8-bom")]
        [InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, "utf-16le")]
        [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x41 }, "utf-16be")]
        [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, "utf-32le")]
        [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF }, "utf-32be")]
        public void Detect_ByteOrderMark(byte[] bytes, string expectedId)
        {
            Assert.Equal(expectedId, EncodingDetector.Detect(bytes).Id);
        }

        // ----- Detection: heuristic -----

        [Fact]
        public void Detect_EmptyFile_IsUtf8()
        {
            Assert.Same(TextEncoding.Utf8, EncodingDetector.Detect(new InMemoryByteSource(Array.Empty<byte>())));
        }

        [Fact]
        public void Detect_BomlessUtf16Le_FromNullPattern()
        {
            byte[] bytes = Encoding.Unicode.GetBytes("Hello, world!"); // no BOM from GetBytes
            Assert.Same(TextEncoding.Utf16Le, EncodingDetector.Detect(bytes));
        }

        [Fact]
        public void Detect_BomlessUtf16Be_FromNullPattern()
        {
            byte[] bytes = Encoding.BigEndianUnicode.GetBytes("Hello, world!");
            Assert.Same(TextEncoding.Utf16Be, EncodingDetector.Detect(bytes));
        }

        [Fact]
        public void Detect_PlainAscii_IsUtf8()
        {
            Assert.Same(TextEncoding.Utf8, EncodingDetector.Detect(Encoding.ASCII.GetBytes("plain ascii text")));
        }

        [Fact]
        public void Detect_Utf8Multibyte_IsUtf8()
        {
            Assert.Same(TextEncoding.Utf8, EncodingDetector.Detect(Encoding.UTF8.GetBytes("café résumé naïve")));
        }

        [Fact]
        public void Detect_Windows1252SmartQuotes_FallsBackToWindows1252()
        {
            // A "smart quote" byte (0x93) is an invalid UTF-8 lead/continuation, so detection rejects UTF-8.
            byte[] bytes = { (byte)'h', (byte)'i', 0x93, (byte)'y', (byte)'o', 0x94 };
            Assert.Same(TextEncoding.Windows1252, EncodingDetector.Detect(bytes));
        }
    }
}
