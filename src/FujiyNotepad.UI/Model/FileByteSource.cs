using System.IO;
using Microsoft.Win32.SafeHandles;

namespace FujiyNotepad.UI.Model
{
    /// <summary>
    /// <see cref="IByteSource"/> over a file, using the positional <see cref="RandomAccess"/> API
    /// (no memory-mapped file, no <see cref="FileStream"/> position state). Positional reads are
    /// thread-safe, so the same handle serves concurrent viewport/search/index reads.
    /// </summary>
    public sealed class FileByteSource : IByteSource
    {
        private readonly SafeFileHandle handle;

        public long Length { get; }

        public FileByteSource(string path)
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            Length = RandomAccess.GetLength(handle);
        }

        public int Read(long offset, Span<byte> buffer) => RandomAccess.Read(handle, buffer, offset);

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
            => RandomAccess.ReadAsync(handle, buffer, offset, token);

        public void Dispose() => handle.Dispose();
    }
}
