using System.Text;
using FujiyNotepad.Core;
using FujiyNotepad.TestSupport;

namespace FujiyNotepad.Core.Tests
{
    public class CharacterCounterTests
    {
        private static Task<long> Count(byte[] bytes, TextEncoding encoding, int chunkSize = 1 << 20)
            => CharacterCounter.CountAsync(new InMemoryByteSource(bytes), encoding, chunkSize, null, CancellationToken.None);

        [Fact]
        public async Task EmptyFile_IsZero()
        {
            Assert.Equal(0, await Count(Array.Empty<byte>(), TextEncoding.Utf8));
        }

        [Fact]
        public async Task Utf8Ascii_CountsEveryCharacterIncludingNewlines()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("hello\nworld"); // 11 chars (the \n counts)
            Assert.Equal(11, await Count(bytes, TextEncoding.Utf8));
        }

        [Fact]
        public async Task Utf8Multibyte_CountsCharactersNotBytes()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("café"); // 4 chars, 5 bytes
            Assert.Equal(4, await Count(bytes, TextEncoding.Utf8));
        }

        [Fact]
        public async Task MultibyteCharStraddlingChunkBoundary_IsCountedOnce()
        {
            // "café" = 63 61 66 C3 A9; a 4-byte chunk splits 'é' (C3 | A9). The stateful decoder must still
            // count exactly 4 characters.
            byte[] bytes = Encoding.UTF8.GetBytes("café");
            Assert.Equal(4, await Count(bytes, TextEncoding.Utf8, chunkSize: 4));
        }

        [Theory]
        [InlineData("\u4E2D", 1)]
        [InlineData("\u4E2D", 2)]
        [InlineData("\U0001F642", 1)]
        [InlineData("\U0001F642", 2)]
        [InlineData("\U0001F642", 3)]
        public async Task Utf8Multibyte_SplitAtEachByteBoundary_CountsCodeUnits(string character, int bytesBeforeBoundary)
        {
            const int chunkSize = 4;
            string text = new string('x', chunkSize - bytesBeforeBoundary) + character + "y";
            byte[] bytes = Encoding.UTF8.GetBytes(text);

            Assert.Equal(text.Length, await Count(bytes, TextEncoding.Utf8, chunkSize));
        }

        [Fact]
        public async Task PublicUtf8_DefaultChunkBoundary_CountsThreeByteCharacter()
        {
            string text = new string('x', (1 << 20) - 1) + "\u4E2D";
            using var source = new InMemoryByteSource(Encoding.UTF8.GetBytes(text));

            Assert.Equal(1_048_576, await CharacterCounter.CountAsync(source, TextEncoding.Utf8));
        }

        [Fact]
        public async Task Utf16Le_CountsCodeUnits()
        {
            byte[] bytes = TextEncoding.Utf16Le.Encoding.GetBytes("abc"); // 3 chars, 6 bytes
            Assert.Equal(3, await Count(bytes, TextEncoding.Utf16Le));
        }

        [Theory]
        [InlineData(false, "\U0001F642x", 1)]
        [InlineData(false, "\U0001F642x", 2)]
        [InlineData(false, "\U0001F642x", 3)]
        [InlineData(true, "\U0001F642x", 1)]
        [InlineData(true, "\U0001F642x", 2)]
        [InlineData(true, "\U0001F642x", 3)]
        [InlineData(false, "x\u4E2Dy", 3)]
        [InlineData(true, "x\u4E2Dy", 3)]
        public async Task Utf16_SplitSurrogatesAndCodeUnits_CountsCodeUnits(bool bigEndian, string text, int chunkSize)
        {
            TextEncoding encoding = bigEndian ? TextEncoding.Utf16Be : TextEncoding.Utf16Le;

            Assert.Equal(text.Length, await Count(encoding.Encode(text), encoding, chunkSize));
        }

        [Fact]
        public async Task PublicUtf16Le_WithBom_DefaultChunkBoundary_CountsSurrogatePair()
        {
            string text = new string('x', (1 << 19) - 1) + "\U0001F642";
            byte[] bytes = TextEncoding.Utf16Le.Bom.Concat(TextEncoding.Utf16Le.Encode(text)).ToArray();
            using var source = new InMemoryByteSource(bytes);

            Assert.Equal(524_289, await CharacterCounter.CountAsync(source, TextEncoding.Utf16Le));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Utf32_WithBom_SplitCodePoints_CountsCodeUnits(bool bigEndian)
        {
            TextEncoding encoding = bigEndian ? TextEncoding.Utf32Be : TextEncoding.Utf32Le;
            byte[] bytes = encoding.Bom.Concat(encoding.Encode("x\U0001F642")).ToArray();

            Assert.Equal(3, await Count(bytes, encoding, chunkSize: 3));
        }

        [Theory]
        [InlineData(new byte[] { 0x78, 0x78, 0x78, 0xE2, 0x61, 0x62, 0x63, 0x64 }, 8)]
        [InlineData(new byte[] { 0x78, 0x78, 0x78, 0xE2, 0x82 }, 4)]
        public async Task Utf8Fallback_PendingSequenceBeforeAsciiOrEnd_IsCounted(byte[] bytes, int expected)
        {
            Assert.Equal(expected, await Count(bytes, TextEncoding.Utf8, chunkSize: 4));
        }

        [Fact]
        public async Task Utf8Bom_IsNotCounted()
        {
            byte[] bytes = { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i' };
            Assert.Equal(2, await Count(bytes, TextEncoding.Utf8Bom));
        }

        [Fact]
        public async Task Utf16Le_Bom_IsNotCounted()
        {
            byte[] content = TextEncoding.Utf16Le.Encoding.GetBytes("hi");
            byte[] bytes = new byte[] { 0xFF, 0xFE }.Concat(content).ToArray();
            Assert.Equal(2, await Count(bytes, TextEncoding.Utf16Le));
        }

        [Fact]
        public async Task Windows1252_CountsOneCharPerByte()
        {
            byte[] bytes = { (byte)'h', (byte)'i', 0x93, (byte)'y', 0x94 }; // 5 single-byte chars
            Assert.Equal(5, await Count(bytes, TextEncoding.Windows1252));
        }

        [Fact]
        public void IsSingleByte_IsTrueOnlyForWindows1252()
        {
            // The viewer shows a huge file's character count instantly (== byte count) only for a single-byte
            // encoding (issue #39); the multi-byte codecs must decode, so they must report false here.
            Assert.True(TextEncoding.Windows1252.Encoding.IsSingleByte);
            Assert.False(TextEncoding.Utf8.Encoding.IsSingleByte);
            Assert.False(TextEncoding.Utf16Le.Encoding.IsSingleByte);
            Assert.False(TextEncoding.Utf32Le.Encoding.IsSingleByte);
        }

        [Fact]
        public async Task Cancelled_ReturnsEarly()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            long n = await CharacterCounter.CountAsync(new InMemoryByteSource(Encoding.UTF8.GetBytes("hello")), TextEncoding.Utf8, 2, null, cts.Token);
            Assert.True(n < 5);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Count_ReportsProgressAndHonorsCancellationBetweenChunks(bool cancelAfterFirstChunk)
        {
            using var cts = new CancellationTokenSource();
            using var source = new InMemoryByteSource(Encoding.UTF8.GetBytes("abc\u4E2Dy"));
            var reports = new List<int>();
            var progress = new SyncProgress(percent =>
            {
                reports.Add(percent);
                if (cancelAfterFirstChunk && percent > 0)
                {
                    cts.Cancel();
                }
            });

            long total = await CharacterCounter.CountAsync(source, TextEncoding.Utf8, 4, progress, cts.Token);

            Assert.Equal(cancelAfterFirstChunk ? 3 : 5, total);
            Assert.Equal(0, reports[0]);
            Assert.Contains(57, reports);
            Assert.Equal(cancelAfterFirstChunk ? 57 : 100, reports[^1]);
        }

        private sealed class SyncProgress : IProgress<int>
        {
            private readonly Action<int> report;

            public SyncProgress(Action<int> report) => this.report = report;

            public void Report(int value) => report(value);
        }
    }
}
