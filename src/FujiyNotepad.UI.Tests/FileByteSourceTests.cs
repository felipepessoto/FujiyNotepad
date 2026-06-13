using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FujiyNotepad.UI.Model;

namespace FujiyNotepad.UI.Tests
{
    public class FileByteSourceTests
    {
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
        public async Task ViewportWindow_ReadsAndDecodesVisibleLines()
        {
            // Mirrors the viewport read path (GetLengthToFillViewport + GoToOffset's decode): locate
            // the window covering the first N lines, read it, and decode via StreamReader.
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "line one\nline two\nline three\nline four\nline five\n");
                using var source = new FileByteSource(path);
                var searcher = new TextSearcher(source);

                const long startOffset = 0;
                const int linesWanted = 3;
                int remaining = linesWanted;
                long length = source.Length - startOffset;
                await foreach (long newline in searcher.Search(startOffset, new byte[] { (byte)'\n' }))
                {
                    if (--remaining == 0)
                    {
                        length = newline + 1 - startOffset;
                        break;
                    }
                }

                var window = new byte[(int)length];
                int got = source.ReadFull(startOffset, window);
                using var stream = new MemoryStream(window, 0, got);
                using var reader = new StreamReader(stream);

                var lines = new List<string>();
                for (int i = 0; i < linesWanted; i++)
                {
                    string? line = reader.ReadLine();
                    if (line is null)
                    {
                        break;
                    }
                    lines.Add(line);
                }

                Assert.Equal(new[] { "line one", "line two", "line three" }, lines);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
