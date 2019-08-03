using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading;
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

        public async IAsyncEnumerable<long> Search(long startOffset, char[] charsToSearch, IProgress<int> progress, CancellationToken token)
        {
            int lastReportValue = 0;
            progress.Report(lastReportValue);

            //long bytesToRead = Math.Min(searchSize, FileSize - startOffset);
            var buffer = new byte[charsToSearch.Length - 1];

            using (var stream = mFile.CreateViewStream(startOffset, 0, MemoryMappedFileAccess.Read))
            using (var streamReader = new StreamReader(stream))
            {
                int byteRead;
                do
                {
                    byteRead = stream.ReadByte();
                    var currentPosition = stream.Position;
                    if (byteRead == charsToSearch[0])
                    {
                        bool equals = true;
                        await stream.ReadAsync(buffer, 0, charsToSearch.Length - 1);

                        for (int i = 0; i < charsToSearch.Length - 1; i++)
                        {
                            if (buffer[i] != charsToSearch[i + 1])//TODO case insensitive
                            {
                                equals = false;
                                break;
                            }
                        }

                        if (equals)
                        {
                            yield return startOffset + currentPosition - 1;// stream.Position - 1;
                        }
                        else
                        {
                            stream.Position = currentPosition;
                        }
                    }

                    long totalBytes = FileSize - startOffset;
                    long currentProgress = currentPosition - startOffset;
                    int progressValue = (int)(currentProgress * 100 / totalBytes);
                    if (lastReportValue != progressValue)
                    {
                        lastReportValue = progressValue;
                        progress.Report(progressValue);
                    }

                } while (byteRead > -1 && token.IsCancellationRequested == false);
            }
        }


        //TODO mudar para search backward
        public IEnumerable<long> SearchBackward(long startOffset, char charToSearch, IProgress<int> progress)
        {
            if (startOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startOffset), $"{nameof(startOffset)} cannot be negative");
            }

            int lastReportValue = 0;
            progress.Report(lastReportValue);

            long searchBackOffset = startOffset;

            do
            {
                long searchSizePerIteration = Math.Min(searchSize, searchBackOffset);
                searchBackOffset = searchBackOffset - searchSizePerIteration;// Math.Max(searchBackOffset - searchSizePerIteration, 0);

                if (searchSizePerIteration > 0)
                {
                    using (var stream = mFile.CreateViewStream(searchBackOffset, searchSizePerIteration, MemoryMappedFileAccess.Read))
                    using (var streamReader = new StreamReader(stream))
                    {
                        stream.Seek(0, SeekOrigin.End);

                        while (stream.Position > 0)
                        {
                            stream.Seek(-1, SeekOrigin.Current);
                            if (stream.ReadByte() == charToSearch)
                            {
                                yield return searchBackOffset + stream.Position - 1;
                            }
                            stream.Seek(-1, SeekOrigin.Current);
                        }
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
            if (charToSearch == '\n')
            {
                yield return -1;
            }
        }
    }
}
