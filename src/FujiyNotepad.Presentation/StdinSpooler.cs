namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Spools a (non-seekable) input stream — typically the process's standard input — into an output stream,
    /// flushing after each chunk so a reader tailing the output (the viewer's Follow Tail, #28) sees it grow
    /// incrementally (issue #103). The engine needs random access, which a pipe cannot provide, so the host
    /// points the output at a temp file and opens that. Pure stream-to-stream copy, unit-tested with in-memory
    /// streams.
    /// </summary>
    public static class StdinSpooler
    {
        public const int DefaultBufferSize = 64 * 1024;

        /// <summary>
        /// Copies <paramref name="input"/> to <paramref name="output"/> in chunks, flushing after each, until the
        /// input ends or the token is cancelled. Returns the number of bytes copied.
        /// </summary>
        public static async Task<long> SpoolAsync(Stream input, Stream output, CancellationToken token = default, int bufferSize = DefaultBufferSize)
        {
            byte[] buffer = new byte[Math.Max(1, bufferSize)];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false); // make growth visible to the tailing reader
                total += read;
            }
            return total;
        }
    }
}
