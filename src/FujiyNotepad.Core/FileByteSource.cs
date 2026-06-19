using Microsoft.Win32.SafeHandles;

namespace FujiyNotepad.Core
{
    /// <summary>
    /// <see cref="IByteSource"/> over a file, using the positional <see cref="RandomAccess"/> API
    /// (no memory-mapped file, no <see cref="FileStream"/> position state). Positional reads are
    /// thread-safe, so the same handle serves concurrent viewport/search/index reads.
    /// </summary>
    public sealed class FileByteSource : IByteSource
    {
        private readonly SafeFileHandle handle;

        public long Length { get; private set; }

        public FileByteSource(string path)
        {
            // Share ReadWrite|Delete so the file can be opened while another process is appending to it
            // (live logs) and can be rotated/deleted underneath us; our positional handle keeps reading.
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
            Length = RandomAccess.GetLength(handle);
        }

        /// <summary>Re-reads the file length from the handle (observing appends or truncation) and returns it.</summary>
        public long RefreshLength() => Length = RandomAccess.GetLength(handle);

        public int Read(long offset, Span<byte> buffer) => RandomAccess.Read(handle, buffer, offset);

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
            => RandomAccess.ReadAsync(handle, buffer, offset, token);

        public void Dispose() => handle.Dispose();
    }
}
