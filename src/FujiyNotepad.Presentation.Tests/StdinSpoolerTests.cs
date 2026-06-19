using System.Text;

namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the stdin spooler (issue #103): a full copy, an empty input, and cancellation of a blocked read.
    /// </summary>
    public class StdinSpoolerTests
    {
        [Fact]
        public async Task SpoolAsync_CopiesAllBytes_AcrossManyChunks()
        {
            byte[] data = Encoding.ASCII.GetBytes("line1\nline2\nline3\ntail-without-newline");
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();

            long n = await StdinSpooler.SpoolAsync(input, output, bufferSize: 4); // tiny buffer -> many chunks

            Assert.Equal(data.Length, n);
            Assert.Equal(data, output.ToArray());
        }

        [Fact]
        public async Task SpoolAsync_EmptyInput_CopiesNothing()
        {
            using var input = new MemoryStream(System.Array.Empty<byte>());
            using var output = new MemoryStream();

            long n = await StdinSpooler.SpoolAsync(input, output);

            Assert.Equal(0, n);
            Assert.Empty(output.ToArray());
        }

        [Fact]
        public async Task SpoolAsync_Cancellation_StopsCopying()
        {
            using var output = new MemoryStream();
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => StdinSpooler.SpoolAsync(new BlockingStream(), output, cts.Token));
        }

        // A read that never returns until cancelled (models a live producer with no data yet).
        private sealed class BlockingStream : Stream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
