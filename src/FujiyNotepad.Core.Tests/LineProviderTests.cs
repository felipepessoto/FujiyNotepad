using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests
{
    public class LineProviderTests
    {
        private static async Task<LineProvider> BuildAsync(IByteSource source)
        {
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            return new LineProvider(source, indexer);
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
        public async Task Utf8Bom_StrippedFromFirstLine()
        {
            byte[] bytes = { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i' };
            var provider = await BuildAsync(new InMemoryByteSource(bytes));

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
    }
}
