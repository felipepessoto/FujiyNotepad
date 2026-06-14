namespace FujiyNotepad.Core
{
    /// <summary>
    /// Read-only positional access to a byte store (a file on disk, or in-memory data for tests).
    /// Implementations must allow concurrent reads at different offsets (no shared position state),
    /// so a background search/index and the UI's viewport read can run without locking.
    /// </summary>
    public interface IByteSource : IDisposable
    {
        long Length { get; }

        /// <summary>
        /// Reads up to <paramref name="buffer"/>.Length bytes starting at <paramref name="offset"/>.
        /// Returns the number of bytes read, which is 0 at end of stream and may be fewer than requested.
        /// </summary>
        int Read(long offset, Span<byte> buffer);

        /// <summary>Asynchronous counterpart of <see cref="Read"/>.</summary>
        ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default);
    }
}
