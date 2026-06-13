using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

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

        public async IAsyncEnumerable<long> Search(long startOffset, char[] charsToSearch, IProgress<int> progress, [EnumeratorCancellation] CancellationToken token)
        {
            int lastReportValue = 0;
            progress.Report(lastReportValue);

            int remaining = charsToSearch.Length - 1;
            var buffer = new byte[remaining];

            using (var stream = mFile.CreateViewStream(startOffset, 0, MemoryMappedFileAccess.Read))
            {
                int byteRead;
                // TODO(perf): scanning one byte at a time via ReadByte is slow on large files. Replace with
                // chunked buffered reads + vectorized ReadOnlySpan<byte>.IndexOf. Tracked as a separate task.
                do
                {
                    byteRead = stream.ReadByte();
                    long currentPosition = stream.Position;
                    if (byteRead == charsToSearch[0])
                    {
                        // Read the remaining bytes fully; a short read (near EOF) cannot be a match.
                        // The token is intentionally not forwarded: cancellation is cooperative (observed by
                        // the loop guard below), so Search never throws and callers can treat a cancelled
                        // search as a normal, empty result.
                        int totalRead = await stream.ReadAtLeastAsync(buffer, remaining, throwOnEndOfStream: false);
                        bool equals = totalRead >= remaining;

                        for (int i = 0; equals && i < remaining; i++)
                        {
                            if (buffer[i] != charsToSearch[i + 1])//TODO case insensitive
                            {
                                equals = false;
                            }
                        }

                        if (equals)
                        {
                            yield return startOffset + currentPosition - 1;
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
