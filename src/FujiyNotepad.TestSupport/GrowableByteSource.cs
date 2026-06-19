using System.Text;
using FujiyNotepad.Core;

namespace FujiyNotepad.TestSupport
{
    /// <summary>
    /// A growable in-memory <see cref="IByteSource"/> for tail/follow tests: <see cref="Append(string)"/>
    /// simulates a writer extending the file and <see cref="Truncate"/> a rotation. The reported
    /// <see cref="Length"/> only advances when <see cref="RefreshLength"/> is called, mirroring a file handle
    /// whose cached length is refreshed explicitly (a search bounded by the old length won't see the new bytes
    /// until then). Reads always see the full written content, like a real handle reading what's on disk.
    /// ASCII content keeps the one-byte-per-character mapping the byte-level search relies on.
    /// </summary>
    public sealed class GrowableByteSource : IByteSource
    {
        private readonly List<byte> data = new();
        private long observedLength;

        public GrowableByteSource(string ascii = "")
        {
            Append(ascii);
            observedLength = data.Count; // the initial content is already "on disk" when opened
        }

        public long Length => observedLength;

        public long RefreshLength() => observedLength = data.Count;

        public void Append(string ascii) => data.AddRange(Encoding.ASCII.GetBytes(ascii));

        public void Append(byte[] bytes) => data.AddRange(bytes);

        /// <summary>Shrinks the content to <paramref name="newCount"/> bytes (simulates a truncate/rotation).</summary>
        public void Truncate(int newCount)
        {
            if (newCount >= 0 && newCount < data.Count)
            {
                data.RemoveRange(newCount, data.Count - newCount);
            }
        }

        public int Read(long offset, Span<byte> buffer)
        {
            if (offset < 0 || offset >= data.Count)
            {
                return 0;
            }
            int count = (int)Math.Min(buffer.Length, data.Count - offset);
            for (int i = 0; i < count; i++)
            {
                buffer[i] = data[(int)offset + i];
            }
            return count;
        }

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
            => ValueTask.FromResult(Read(offset, buffer.Span));

        public void Dispose() { }
    }
}
