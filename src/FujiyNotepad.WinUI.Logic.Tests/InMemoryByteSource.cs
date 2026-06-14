using System.Text;
using FujiyNotepad.Core;

namespace FujiyNotepad.WinUI.Logic.Tests
{
    /// <summary>
    /// In-memory <see cref="IByteSource"/> for fast, file-free engine tests. ASCII content keeps a
    /// one-byte-per-character mapping that matches the byte-level search.
    /// </summary>
    internal sealed class InMemoryByteSource : IByteSource
    {
        private readonly byte[] data;

        public InMemoryByteSource(byte[] data) => this.data = data;

        public InMemoryByteSource(string ascii) => data = Encoding.ASCII.GetBytes(ascii);

        public long Length => data.Length;

        public int Read(long offset, Span<byte> buffer)
        {
            if (offset < 0 || offset >= data.Length)
            {
                return 0;
            }
            int count = (int)Math.Min(buffer.Length, data.Length - offset);
            data.AsSpan((int)offset, count).CopyTo(buffer);
            return count;
        }

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
            => ValueTask.FromResult(Read(offset, buffer.Span));

        public void Dispose()
        {
        }
    }
}
