using System.Text;
using FujiyNotepad.Core;

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

        [Fact]
        public async Task Utf16Le_CountsCodeUnits()
        {
            byte[] bytes = TextEncoding.Utf16Le.Encoding.GetBytes("abc"); // 3 chars, 6 bytes
            Assert.Equal(3, await Count(bytes, TextEncoding.Utf16Le));
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
        public async Task Cancelled_ReturnsEarly()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            long n = await CharacterCounter.CountAsync(new InMemoryByteSource(Encoding.UTF8.GetBytes("hello")), TextEncoding.Utf8, 2, null, cts.Token);
            Assert.True(n < 5);
        }
    }
}
