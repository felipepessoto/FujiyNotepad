using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FujiyNotepad.UI.Model
{
    public class TextSearcher
    {
        long searchSize = 1024 * 1024;
        private readonly MemoryMappedFile mFile;
        private readonly long fileSize;

        public TextSearcher(MemoryMappedFile file, long fileSize)
        {
            this.mFile = file;
            this.fileSize = fileSize;
        }

        public IEnumerable<long> SearchInFile(long startOffset, char charToSearch)
        {
            do
            {
                long bytesToRead = Math.Min(searchSize, fileSize - startOffset);

                using (var stream = mFile.CreateViewStream(startOffset, bytesToRead, MemoryMappedFileAccess.Read))
                using (var streamReader = new StreamReader(stream))
                {
                    int byteRead;
                    do
                    {
                        byteRead = stream.ReadByte();
                        if (byteRead == charToSearch)
                        {
                            yield return startOffset + stream.Position;
                        }
                    } while (byteRead > -1);
                }
                startOffset += bytesToRead;
            }
            while (startOffset < fileSize);
        }

        public long SearchNewLineBefore(long startOffset)
        {
            if (startOffset == 0)
            {
                return 0;
            }
            long searchBackOffset = startOffset;
            long searchSizePerIteration = Math.Min(searchSize, startOffset);

            do
            {
                searchBackOffset = Math.Max(searchBackOffset - searchSizePerIteration, 0);

                using (var stream = mFile.CreateViewStream(searchBackOffset, searchSizePerIteration, MemoryMappedFileAccess.Read))
                using (var streamReader = new StreamReader(stream))
                {
                    var newLineAt = streamReader.ReadToEnd().LastIndexOf('\n');

                    if (newLineAt > -1)
                    {
                        return searchBackOffset + newLineAt + 1;
                    }


                    /*
                    stream.Seek(0, SeekOrigin.End);

                    while (stream.Position > 0)
                    {
                        stream.Seek(-1, SeekOrigin.Current);
                        if(stream.ReadByte() == '\n')
                        {
                            return currentPosition;
                        }
                        stream.Seek(-1, SeekOrigin.Current);
                    }
                    */
                }
            }
            while (searchBackOffset > 0);

            return 0;
        }
    }
}
