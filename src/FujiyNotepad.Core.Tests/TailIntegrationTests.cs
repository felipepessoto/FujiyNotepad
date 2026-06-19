using FujiyNotepad.TestSupport;

namespace FujiyNotepad.Core.Tests
{
    /// <summary>
    /// Tests the tail/follow plumbing (issue #28) over a growable in-memory source: appended lines appear after
    /// a length refresh + resume-index, the previously-final unterminated line is re-read (extended / newly
    /// terminated), and a truncation is observed as a size shrink. Fully headless — no timer, no UI.
    /// </summary>
    public class TailIntegrationTests
    {
        private static async Task<(LineProvider provider, LineIndexer indexer)> IndexAsync(GrowableByteSource source)
        {
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            var provider = new LineProvider(source, indexer);
            return (provider, indexer);
        }

        // Resumes the index over the appended region exactly the way the tail poller does on a grow.
        private static async Task GrowAsync(GrowableByteSource source, LineIndexer indexer, LineProvider provider)
        {
            source.RefreshLength();          // observe the new bytes (the searcher is bounded by this)
            indexer.IsCompleted = false;     // re-arm
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            provider.RefreshLength();        // update size / endsWithNewline, drop stale cache
        }

        [Fact]
        public async Task AppendedLines_AppearAfterResumeIndex()
        {
            var source = new GrowableByteSource("line1\nline2\n");
            (LineProvider provider, LineIndexer indexer) = await IndexAsync(source);
            Assert.Equal(2, provider.LineCount);

            source.Append("line3\nline4\n");
            await GrowAsync(source, indexer, provider);

            Assert.Equal(4, provider.LineCount);
            Assert.Equal("line3", provider.GetLine(2));
            Assert.Equal("line4", provider.GetLine(3));
        }

        [Fact]
        public async Task FinalUnterminatedLine_IsExtendedThenTerminated_OnAppend()
        {
            var source = new GrowableByteSource("a\nb");   // "b" is the final, unterminated line
            (LineProvider provider, LineIndexer indexer) = await IndexAsync(source);
            Assert.Equal(2, provider.LineCount);
            Assert.Equal("b", provider.GetLine(1));

            source.Append("cd\n");                          // "b" -> "bcd\n": the open line grows and terminates
            await GrowAsync(source, indexer, provider);

            Assert.Equal(2, provider.LineCount);
            Assert.Equal("bcd", provider.GetLine(1));        // the previously-final line was re-read
        }

        [Fact]
        public async Task NewlineThenLine_AfterUnterminatedTail_AddsALine()
        {
            var source = new GrowableByteSource("a\nb");
            (LineProvider provider, LineIndexer indexer) = await IndexAsync(source);
            Assert.Equal(2, provider.LineCount);

            source.Append("\nc\n");                          // "a\nb\nc\n": b terminates, c is new
            await GrowAsync(source, indexer, provider);

            Assert.Equal(3, provider.LineCount);
            Assert.Equal("b", provider.GetLine(1));
            Assert.Equal("c", provider.GetLine(2));
        }

        [Fact]
        public async Task Truncation_IsObservedAsAShrink()
        {
            var source = new GrowableByteSource("line1\nline2\nline3\n");
            (LineProvider provider, _) = await IndexAsync(source);
            Assert.Equal(3, provider.LineCount);
            long full = source.Length;

            source.Truncate(6);                              // back to just "line1\n"
            Assert.True(source.RefreshLength() < full);      // a shrink the poller detects -> reset/reload
            Assert.True(provider.RefreshLength());           // the provider observes the size change
        }
    }
}
