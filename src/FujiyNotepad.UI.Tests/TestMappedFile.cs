using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace FujiyNotepad.UI.Tests
{
    /// <summary>
    /// Writes ASCII content to a temporary file and exposes a read-only <see cref="MemoryMappedFile"/>
    /// over it. ASCII is used so each character maps to a single byte, matching the byte-level
    /// comparison performed by <c>TextSearcher</c>.
    /// </summary>
    internal sealed class TestMappedFile : IDisposable
    {
        private readonly string path;

        public MemoryMappedFile Mmf { get; }
        public long Size { get; }

        public TestMappedFile(string asciiContent)
        {
            path = Path.GetTempFileName();
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(asciiContent));
            Size = new FileInfo(path).Length;
            Mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        }

        public void Dispose()
        {
            Mmf.Dispose();
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best-effort cleanup; the OS removes orphaned temp files eventually.
            }
        }
    }
}
