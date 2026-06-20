using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FujiyNotepad.Core;
using FujiyNotepad.TestSupport;

namespace FujiyNotepad.Core.Tests
{
    public class LineProviderTests
    {
        private static async Task<LineProvider> BuildAsync(IByteSource source, TextEncoding? encoding = null)
        {
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher, encoding);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            return new LineProvider(source, indexer, encoding);
        }

        private static Task<LineProvider> BuildAsync(string ascii) => BuildAsync(new InMemoryByteSource(ascii));

        [Fact]
        public async Task NoTrailingNewline_SplitsEveryLine()
        {
            var provider = await BuildAsync("ab\ncd\nef");

            Assert.Equal(3, provider.LineCount);
            Assert.Equal("ab", provider.GetLine(0));
            Assert.Equal("cd", provider.GetLine(1));
            Assert.Equal("ef", provider.GetLine(2));
        }

        [Fact]
        public async Task SingleTrailingNewline_DoesNotAddPhantomLine()
        {
            var provider = await BuildAsync("a\nb\n");

            Assert.Equal(2, provider.LineCount);
            Assert.Equal("a", provider.GetLine(0));
            Assert.Equal("b", provider.GetLine(1));
        }

        [Fact]
        public async Task BlankLinesArePreserved()
        {
            var provider = await BuildAsync("a\n\nb");

            Assert.Equal(3, provider.LineCount);
            Assert.Equal("a", provider.GetLine(0));
            Assert.Equal("", provider.GetLine(1));
            Assert.Equal("b", provider.GetLine(2));
        }

        [Fact]
        public async Task DoubleTrailingNewline_KeepsOneEmptyLine()
        {
            var provider = await BuildAsync("a\n\n");

            Assert.Equal(2, provider.LineCount);
            Assert.Equal("a", provider.GetLine(0));
            Assert.Equal("", provider.GetLine(1));
        }

        [Fact]
        public async Task SingleLine_NoNewline()
        {
            var provider = await BuildAsync("abc");

            Assert.Equal(1, provider.LineCount);
            Assert.Equal("abc", provider.GetLine(0));
        }

        [Fact]
        public async Task EmptyFile_HasNoLines()
        {
            var provider = await BuildAsync("");

            Assert.Equal(0, provider.LineCount);
        }

        [Fact]
        public async Task CarriageReturnIsStripped()
        {
            var provider = await BuildAsync("x\r\ny");

            Assert.Equal(2, provider.LineCount);
            Assert.Equal("x", provider.GetLine(0));
            Assert.Equal("y", provider.GetLine(1));
        }

        [Fact]
        public async Task GetLineEnding_DistinguishesLfCrLfAndNone()
        {
            // Line 0 ends with \n, line 1 with \r\n, line 2 is the final unterminated line.
            var provider = await BuildAsync("a\nb\r\nc");

            Assert.Equal(3, provider.LineCount);
            Assert.Equal(LineEnding.Lf, provider.GetLineEnding(0));
            Assert.Equal(LineEnding.CrLf, provider.GetLineEnding(1));
            Assert.Equal(LineEnding.None, provider.GetLineEnding(2));
        }

        [Fact]
        public async Task GetLineEnding_EmptyLines()
        {
            // A blank LF line and a blank CRLF line.
            var provider = await BuildAsync("\n\r\nx");

            Assert.Equal(LineEnding.Lf, provider.GetLineEnding(0));
            Assert.Equal(LineEnding.CrLf, provider.GetLineEnding(1));
        }

        [Fact]
        public async Task GetLineEnding_Utf16_DetectsCrLf()
        {
            // UTF-16LE "a\r\nb": 'a' 00, '\r' 00, '\n' 00, 'b' 00.
            byte[] bytes = { (byte)'a', 0, 0x0D, 0, 0x0A, 0, (byte)'b', 0 };
            var provider = await BuildAsync(new InMemoryByteSource(bytes), TextEncoding.Utf16Le);

            Assert.Equal(LineEnding.CrLf, provider.GetLineEnding(0));
            Assert.Equal(LineEnding.None, provider.GetLineEnding(1));
        }

        [Fact]
        public async Task Utf8Bom_StrippedFromFirstLine()
        {
            byte[] bytes = { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i' };
            var provider = await BuildAsync(new InMemoryByteSource(bytes), TextEncoding.Utf8Bom);

            Assert.Equal(1, provider.LineCount);
            Assert.Equal("hi", provider.GetLine(0));
        }

        [Fact]
        public async Task ByteColumnToCharColumn_MapsAcrossMultibyteChars()
        {
            // "aébc" in UTF-8: a=1 byte, é=2 bytes (0xC3 0xA9), b=1, c=1.
            byte[] bytes = Encoding.UTF8.GetBytes("aébc");
            var provider = await BuildAsync(new InMemoryByteSource(bytes));

            Assert.Equal("aébc", provider.GetLine(0));
            Assert.Equal(0, provider.ByteColumnToCharColumn(0, 0));
            Assert.Equal(1, provider.ByteColumnToCharColumn(0, 1)); // after 'a' (1 byte) -> 1 char
            Assert.Equal(2, provider.ByteColumnToCharColumn(0, 3)); // after 'a'+'é' (3 bytes) -> 2 chars
            Assert.Equal(3, provider.ByteColumnToCharColumn(0, 4)); // after 'a'+'é'+'b' (4 bytes) -> 3 chars
        }

        [Fact]
        public async Task ByteColumnToCharColumn_FarByteColumnIntoGiantLine_IsBoundedNotProportional()
        {
            // A single line far longer than the per-line cap (no newline). A Find match deep into it must
            // not allocate/read the whole distance; the column is bounded by the capped, truncated line.
            const int maxLineBytes = 64 * 1024;
            var provider = await BuildAsync(new string('a', maxLineBytes * 4));

            int column = provider.ByteColumnToCharColumn(0, 50_000_000);

            Assert.True(column > 0);
            Assert.True(column <= maxLineBytes, $"expected a bounded column, got {column}");
        }

        [Fact]
        public async Task CharColumnToByteColumn_MapsAcrossMultibyteChars()
        {
            // Inverse of ByteColumnToCharColumn for "aébc" (é is 2 UTF-8 bytes); total is 5 bytes.
            byte[] bytes = Encoding.UTF8.GetBytes("aébc");
            var provider = await BuildAsync(new InMemoryByteSource(bytes));

            Assert.Equal(0, provider.CharColumnToByteColumn(0, 0));
            Assert.Equal(1, provider.CharColumnToByteColumn(0, 1)); // after 'a' -> 1 byte
            Assert.Equal(3, provider.CharColumnToByteColumn(0, 2)); // after 'a'+'é' -> 3 bytes
            Assert.Equal(4, provider.CharColumnToByteColumn(0, 3)); // after 'a'+'é'+'b' -> 4 bytes
            Assert.Equal(5, provider.CharColumnToByteColumn(0, 99)); // clamped to the 4-char / 5-byte line
        }

        // ----- Non-UTF-8 encodings -----

        [Theory]
        [InlineData("utf-16le")]
        [InlineData("utf-16be")]
        [InlineData("utf-32le")]
        [InlineData("utf-32be")]
        public async Task MultiByteEncoding_SplitsAndDecodesLines(string id)
        {
            TextEncoding enc = TextEncoding.FromId(id);
            byte[] bytes = enc.Encoding.GetBytes("ab\ncd\nef"); // no BOM
            var provider = await BuildAsync(new InMemoryByteSource(bytes), enc);

            Assert.Equal(3, provider.LineCount);
            Assert.Equal("ab", provider.GetLine(0));
            Assert.Equal("cd", provider.GetLine(1));
            Assert.Equal("ef", provider.GetLine(2));
        }

        [Fact]
        public async Task Utf16Le_WithBom_StripsBomAndSplitsLines()
        {
            byte[] content = TextEncoding.Utf16Le.Encoding.GetBytes("hi\nyo");
            byte[] bytes = new byte[] { 0xFF, 0xFE }.Concat(content).ToArray();
            var provider = await BuildAsync(new InMemoryByteSource(bytes), TextEncoding.Utf16Le);

            Assert.Equal(2, provider.LineCount);
            Assert.Equal("hi", provider.GetLine(0));
            Assert.Equal("yo", provider.GetLine(1));
        }

        [Fact]
        public async Task Utf16Le_CrlfTerminatorIsStripped()
        {
            byte[] bytes = TextEncoding.Utf16Le.Encoding.GetBytes("ab\r\ncd");
            var provider = await BuildAsync(new InMemoryByteSource(bytes), TextEncoding.Utf16Le);

            Assert.Equal(2, provider.LineCount);
            Assert.Equal("ab", provider.GetLine(0));
            Assert.Equal("cd", provider.GetLine(1));
        }

        [Fact]
        public async Task Utf16Le_TrailingNewline_NoPhantomLine()
        {
            byte[] bytes = TextEncoding.Utf16Le.Encoding.GetBytes("a\nb\n");
            var provider = await BuildAsync(new InMemoryByteSource(bytes), TextEncoding.Utf16Le);

            Assert.Equal(2, provider.LineCount);
            Assert.Equal("b", provider.GetLine(1));
        }

        [Fact]
        public async Task Utf16Le_DoesNotSplitOnAByteThatStraddlesTwoCharacters()
        {
            // U+0A41 (bytes "41 0A") then U+6E00 (bytes "00 6E") put the byte pair "0A 00" at an ODD offset,
            // which looks like a UTF-16 newline only when mis-aligned. It stays one line because line
            // splitting honours code-unit alignment.
            byte[] bytes = TextEncoding.Utf16Le.Encoding.GetBytes("\u0A41\u6E00");
            var provider = await BuildAsync(new InMemoryByteSource(bytes), TextEncoding.Utf16Le);

            Assert.Equal(1, provider.LineCount);
            Assert.Equal("\u0A41\u6E00", provider.GetLine(0));
        }

        [Fact]
        public async Task Windows1252_DecodesSmartPunctuation()
        {
            byte[] bytes = { (byte)'h', (byte)'i', 0x93, (byte)'y', (byte)'o', 0x94 };
            var provider = await BuildAsync(new InMemoryByteSource(bytes), TextEncoding.Windows1252);

            Assert.Equal(1, provider.LineCount);
            Assert.Equal("hi“yo”", provider.GetLine(0));
        }

        [Fact]
        public async Task Utf16Le_ColumnMappingRoundTrips()
        {
            byte[] bytes = TextEncoding.Utf16Le.Encoding.GetBytes("abc");
            var provider = await BuildAsync(new InMemoryByteSource(bytes), TextEncoding.Utf16Le);

            Assert.Equal("abc", provider.GetLine(0));
            Assert.Equal(1, provider.ByteColumnToCharColumn(0, 2)); // 2 bytes = 1 UTF-16 char
            Assert.Equal(2, provider.ByteColumnToCharColumn(0, 4)); // 4 bytes = 2 chars
            Assert.Equal(4, provider.CharColumnToByteColumn(0, 2)); // 2 chars = 4 bytes
        }

        [Fact]
        public async Task GetLineUncached_ReturnsSameTextAsGetLine()
        {
            var provider = await BuildAsync("alpha\nbeta\ngamma\n");

            Assert.Equal("alpha", provider.GetLineUncached(0));
            Assert.Equal("beta", provider.GetLineUncached(1));
            Assert.Equal("gamma", provider.GetLineUncached(2));
        }

        [Fact]
        public async Task GetLineUncached_ServesAnAlreadyCachedLineWithoutReading()
        {
            var counting = new CountingByteSource(new InMemoryByteSource("alpha\nbeta\ngamma\n"));
            var provider = await BuildAsync(counting);

            provider.GetLine(1); // caches line 1
            int before = counting.Reads;
            Assert.Equal("beta", provider.GetLineUncached(1)); // cache hit
            Assert.Equal(before, counting.Reads); // no source read
        }

        [Fact]
        public async Task GetLineUncached_DoesNotPopulateCache_SoRepeatsReReadTheSource()
        {
            var counting = new CountingByteSource(new InMemoryByteSource("alpha\nbeta\ngamma\ndelta\n"));
            var provider = await BuildAsync(counting);

            // Prime the index block for line 2 so the reads below are line-content reads, not index expansion.
            Assert.Equal("gamma", provider.GetLineUncached(2));

            // A second uncached read of the same (never-cached) line still hits the source — it was not stored.
            int before = counting.Reads;
            Assert.Equal("gamma", provider.GetLineUncached(2));
            int afterFirst = counting.Reads;
            Assert.Equal("gamma", provider.GetLineUncached(2));
            int afterSecond = counting.Reads;

            Assert.True(afterFirst > before, "uncached read should read the source");
            Assert.True(afterSecond > afterFirst, "a repeat uncached read should read the source again (not cached)");

            // Contrast: GetLine caches, so a repeat does no further read.
            provider.GetLine(3);
            int cached = counting.Reads;
            Assert.Equal("delta", provider.GetLine(3));
            Assert.Equal(cached, counting.Reads);
        }

        // Wraps a byte source to count positional reads, so a test can observe whether a line was cached.
        private sealed class CountingByteSource : IByteSource
        {
            private readonly IByteSource inner;
            public int Reads;

            public CountingByteSource(IByteSource inner) => this.inner = inner;

            public long Length => inner.Length;

            public long RefreshLength() => inner.RefreshLength();

            public int Read(long offset, Span<byte> buffer)
            {
                Reads++;
                return inner.Read(offset, buffer);
            }

            public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
            {
                Reads++;
                return inner.ReadAsync(offset, buffer, token);
            }

            public void Dispose() => inner.Dispose();
        }
    }
}
