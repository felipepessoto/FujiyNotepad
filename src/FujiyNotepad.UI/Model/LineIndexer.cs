namespace FujiyNotepad.UI.Model
{
    public class LineIndexer
    {
        List<long> lineNumberIndex = new List<long>();
        public static char[] LineBreakChar = new[] { '\n' };//ReadOnlySpan<char>

        private readonly TextSearcher searcher;

        public bool IsCompleted { get; set; }

        public LineIndexer(TextSearcher searcher)
        {
            this.searcher = searcher;
        }

        public async Task StartTaskToIndexLines(CancellationToken cancelToken, IProgress<int> progress)
        {
            long startOffset = 0;

            if (lineNumberIndex.Count == 0)
            {
                lineNumberIndex.Add(0);
                lineNumberIndex.Add(0);
            }
            else
            {
                startOffset = lineNumberIndex[lineNumberIndex.Count - 1];
            }

            // Pass None to Search so it keeps yielding line breaks; cancellation is observed here via
            // ThrowIfCancellationRequested, which signals a stop by throwing OperationCanceledException
            // (caught by the caller to re-enable resuming and to avoid marking a partial index complete).
            await foreach (long result in searcher.Search(startOffset, LineBreakChar, progress, CancellationToken.None))
            {
                cancelToken.ThrowIfCancellationRequested();
                lineNumberIndex.Add(result + 1);
            }

            IsCompleted = true;
        }

        public long GetOffsetFromLineNumber(int lineNumber)
        {
            if (lineNumberIndex.Count > lineNumber)
            {
                long offset = lineNumberIndex[lineNumber];

                return offset;
            }
            else
            {
                throw new InvalidOperationException();
            }
        }

        public int GetNumberOfLinesIndexed()
        {
            return lineNumberIndex.Count;
        }
    }
}
