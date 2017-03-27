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
        public long FileSize { get; }

        public TextSearcher(MemoryMappedFile file, long fileSize)
        {
            this.mFile = file;
            FileSize = fileSize;
        }

        public IEnumerable<long> Search(long startOffset, char charToSearch, IProgress<int> progress)
        {
            int lastReportValue = 0;// (int)(startOffset * 100 / FileSize);
            progress.Report(lastReportValue);

            //TODO validar se startOffset é o tamanho do arquivo?

            do
            {
                long bytesToRead = Math.Min(searchSize, FileSize - startOffset);

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

                int progressValue = (int)(startOffset * 100 / FileSize);
                if (lastReportValue != progressValue)
                {
                    lastReportValue = progressValue;
                    progress.Report(progressValue);
                }
            }
            while (startOffset < FileSize);
        }


        //TODO mudar para search backward
        public IEnumerable<long> SearchBackward(long startOffset, char charToSearch, IProgress<int> progress)
        {
            if(startOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startOffset), $"{nameof(startOffset)} cannot be negative");
            }
            if (startOffset == 0)
            {
                yield break;
            }

            int lastReportValue = 0;
            progress.Report(lastReportValue);

            long searchBackOffset = startOffset;

            do
            {
                long searchSizePerIteration = Math.Min(searchSize, searchBackOffset);
                searchBackOffset = searchBackOffset - searchSizePerIteration;// Math.Max(searchBackOffset - searchSizePerIteration, 0);

                using (var stream = mFile.CreateViewStream(searchBackOffset, searchSizePerIteration, MemoryMappedFileAccess.Read))
                using (var streamReader = new StreamReader(stream))
                {
                    stream.Seek(0, SeekOrigin.End);

                    while (stream.Position > 0)
                    {
                        stream.Seek(-1, SeekOrigin.Current);
                        if(stream.ReadByte() == charToSearch)
                        {
                            yield return stream.Position;
                        }
                        stream.Seek(-1, SeekOrigin.Current);
                    }                    
                }

                int progressValue = (int)((FileSize - startOffset) * 100 / FileSize);
                if (lastReportValue != progressValue)
                {
                    lastReportValue = progressValue;
                    progress.Report(progressValue);
                }
            }
            while (searchBackOffset > 0);

            //Implicit new line at file start
            if(charToSearch == '\n')
            {
                yield return 0;
            }
        }
    }
}
