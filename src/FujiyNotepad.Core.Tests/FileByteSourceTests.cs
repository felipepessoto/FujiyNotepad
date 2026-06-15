using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests
{
    public class FileByteSourceTests
    {
        [Fact]
        public void CanOpen_WhileAnotherProcessHasItOpenForWriting()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("hello"));

                // Simulate a log writer that keeps the file open for appending. With FileShare.Read the
                // viewer could not open it (sharing violation); FileShare.ReadWrite|Delete allows it.
                using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

                using var source = new FileByteSource(path);

                Assert.Equal(5, source.Length);
                var buffer = new byte[5];
                Assert.Equal(5, source.ReadFull(0, buffer));
                Assert.Equal("hello", Encoding.ASCII.GetString(buffer));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Read_ReturnsBytesAtOffset()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("hello world"));
                using var source = new FileByteSource(path);

                Assert.Equal(11, source.Length);

                var buffer = new byte[5];
                int read = source.ReadFull(6, buffer);

                Assert.Equal(5, read);
                Assert.Equal("world", Encoding.ASCII.GetString(buffer));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task Search_OverFileByteSource_FindsNewlinesFromDisk()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("ab\ncd\nef"));
                using var source = new FileByteSource(path);
                var searcher = new TextSearcher(source);

                var results = new List<long>();
                await foreach (long offset in searcher.Search(0, new byte[] { (byte)'\n' }))
                {
                    results.Add(offset);
                }

                Assert.Equal(new long[] { 2, 5 }, results);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task LineProvider_OverRealFile_RandomAccessAcrossManyChunks()
        {
            string path = Path.GetTempFileName();
            try
            {
                const int lineCount = 5000;
                var expected = new string[lineCount];
                var builder = new StringBuilder();
                for (int i = 0; i < lineCount; i++)
                {
                    string line = $"line {i} " + new string('x', i % 200);
                    expected[i] = line;
                    builder.Append(line).Append('\n');
                }
                File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                using var source = new FileByteSource(path);
                var searcher = new TextSearcher(source);
                var indexer = new LineIndexer(searcher);
                await indexer.StartTaskToIndexLines(System.Threading.CancellationToken.None, new System.Progress<int>());

                var provider = new LineProvider(source, indexer);

                Assert.Equal(lineCount, provider.LineCount);
                Assert.Equal(expected[0], provider.GetLine(0));
                Assert.Equal(expected[2499], provider.GetLine(2499));
                Assert.Equal(expected[lineCount - 1], provider.GetLine(lineCount - 1));

                long midOffset = indexer.GetOffsetFromLineNumber(2500); // 1-based start of display line 2499
                Assert.Equal(2499, indexer.GetLineNumberFromOffset(midOffset));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
