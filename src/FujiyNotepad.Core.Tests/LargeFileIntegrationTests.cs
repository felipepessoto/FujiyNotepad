using System.Text;
using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests
{
    /// <summary>
    /// End-to-end checks over a multi-million-line file on disk — the scenario the app exists for. The engine
    /// opens, indexes, counts, random-accesses and searches it through <see cref="FileByteSource"/> without
    /// loading the whole file into memory. These guard the large-file behaviour that the device-free unit
    /// tests (small in-memory inputs) can't, and mirror the paths benchmarked in FujiyNotepad.Benchmarks.
    /// </summary>
    public sealed class LargeFileIntegrationTests : IClassFixture<LargeFileIntegrationTests.LargeFile>
    {
        private readonly LargeFile file;

        public LargeFileIntegrationTests(LargeFile file) => this.file = file;

        /// <summary>A real temp file of ~1.5M numbered lines (~45 MB), generated once and shared across tests.</summary>
        public sealed class LargeFile : IDisposable
        {
            public const int Lines = 1_500_000;

            public string Path { get; }
            public long SizeBytes { get; }

            public LargeFile()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fujiy-largefile-{Guid.NewGuid():N}.txt");
                using (var writer = new StreamWriter(Path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    // '\n' explicitly (not WriteLine, which would emit '\r\n') so the line maths is exact.
                    for (int i = 0; i < Lines; i++)
                    {
                        writer.Write("Line ");
                        writer.Write(i);
                        writer.Write(" : the quick brown fox\n");
                    }
                }
                SizeBytes = new FileInfo(Path).Length;
            }

            public void Dispose()
            {
                try { File.Delete(Path); } catch { /* best effort */ }
            }
        }

        private static async Task<(FileByteSource source, LineIndexer indexer, LineProvider provider)> OpenAsync(string path)
        {
            var source = new FileByteSource(path);
            var indexer = new LineIndexer(new TextSearcher(source));
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            var provider = new LineProvider(source, indexer);
            return (source, indexer, provider);
        }

        [Fact]
        public async Task Indexes_AllLines()
        {
            (FileByteSource source, _, LineProvider provider) = await OpenAsync(file.Path);
            using (source)
            {
                Assert.Equal(LargeFile.Lines, provider.LineCount);
            }
        }

        [Fact]
        public async Task RandomAccess_ReturnsTheCorrectLines()
        {
            (FileByteSource source, _, LineProvider provider) = await OpenAsync(file.Path);
            using (source)
            {
                // Jumping straight to far-apart lines exercises checkpoint reconstruction, not a full scan.
                Assert.Equal("Line 0 : the quick brown fox", provider.GetLine(0));
                Assert.Equal("Line 750000 : the quick brown fox", provider.GetLine(750_000));
                Assert.Equal("Line 1234567 : the quick brown fox", provider.GetLine(1_234_567));
                Assert.Equal($"Line {LargeFile.Lines - 1} : the quick brown fox", provider.GetLine(LargeFile.Lines - 1));
            }
        }

        [Fact]
        public async Task Search_FindsLateMatch_AndMapsBackToItsLine()
        {
            (FileByteSource source, LineIndexer indexer, _) = await OpenAsync(file.Path);
            using (source)
            {
                byte[] needle = Encoding.ASCII.GetBytes($"Line {LargeFile.Lines - 1} :");
                long offset = -1;
                await foreach (long o in new TextSearcher(source).Search(0, needle))
                {
                    offset = o;
                    break;
                }

                Assert.True(offset > 0, "needle should be found late in the file");
                Assert.Equal(LargeFile.Lines - 1, indexer.GetLineNumberFromOffset(offset));
            }
        }

        [Fact]
        public async Task Indexing_StaysWellUnderFileSizeInMemory()
        {
            long before = GC.GetTotalMemory(forceFullCollection: true);

            (FileByteSource source, _, LineProvider provider) = await OpenAsync(file.Path);
            using (source)
            {
                // Touch a spread of lines so the provider holds realistic state, then measure retained memory.
                for (int i = 0; i < LargeFile.Lines; i += LargeFile.Lines / 10)
                {
                    _ = provider.GetLine(i);
                }

                long after = GC.GetTotalMemory(forceFullCollection: true);
                long grew = after - before;

                // The sparse index (one offset per 1024 lines) plus bounded caches must stay far below the file
                // size: a regression that loaded the whole file, or stored every line offset, would blow past
                // this. The bound is deliberately generous to stay robust against GC/measurement noise.
                Assert.True(grew < file.SizeBytes / 2,
                    $"managed heap grew {grew:N0} bytes after indexing a {file.SizeBytes:N0}-byte file");
            }
        }
    }
}
