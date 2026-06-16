using System.IO;
using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests
{
    public class MatchingLinesExporterTests
    {
        // Lines plus optional per-line endings; when endings is null the source does NOT implement
        // ILineEndingSource, exercising the fallback separator path.
        private sealed class FakeLines : ILineSource
        {
            private readonly string[] lines;
            public FakeLines(params string[] lines) => this.lines = lines;
            public int LineCount => lines.Length;
            public string GetLine(int lineIndex) => lines[lineIndex];
        }

        private sealed class FakeLinesWithEndings : ILineSource, ILineEndingSource
        {
            private readonly string[] lines;
            private readonly LineEnding[] endings;
            public FakeLinesWithEndings(string[] lines, LineEnding[] endings)
            {
                this.lines = lines;
                this.endings = endings;
            }
            public int LineCount => lines.Length;
            public string GetLine(int lineIndex) => lines[lineIndex];
            public LineEnding GetLineEnding(int lineIndex) => endings[lineIndex];
        }

        [Fact]
        public void BuildClipboardText_Empty_ReturnsEmpty()
        {
            (string text, int count, bool truncated) = MatchingLinesExporter.BuildClipboardText(new FakeLines());

            Assert.Equal(string.Empty, text);
            Assert.Equal(0, count);
            Assert.False(truncated);
        }

        [Fact]
        public void BuildClipboardText_NoEndingSource_JoinsWithLfAndNoTrailingNewline()
        {
            var src = new FakeLines("alpha", "beta", "gamma");

            (string text, int count, bool truncated) = MatchingLinesExporter.BuildClipboardText(src);

            Assert.Equal("alpha\nbeta\ngamma", text);
            Assert.Equal(3, count);
            Assert.False(truncated);
        }

        [Fact]
        public void BuildClipboardText_PreservesPerLineEndings()
        {
            var src = new FakeLinesWithEndings(
                new[] { "win", "unix", "tail" },
                new[] { LineEnding.CrLf, LineEnding.Lf, LineEnding.None });

            (string text, _, _) = MatchingLinesExporter.BuildClipboardText(src);

            // The final line had no terminator, so the output does not gain a trailing newline.
            Assert.Equal("win\r\nunix\ntail", text);
        }

        [Fact]
        public void BuildClipboardText_FinalLineWithEnding_KeepsTrailingNewline()
        {
            var src = new FakeLinesWithEndings(
                new[] { "a", "b" },
                new[] { LineEnding.Lf, LineEnding.CrLf });

            (string text, _, _) = MatchingLinesExporter.BuildClipboardText(src);

            Assert.Equal("a\nb\r\n", text);
        }

        [Fact]
        public void BuildClipboardText_CapTruncatesAndReportsCount()
        {
            var src = new FakeLines("aaaa", "bbbb", "cccc", "dddd");

            // Cap below the full length forces a stop partway; the cap is checked before each line, so the
            // first line that pushes the buffer to/over the cap is the last one included.
            (string text, int count, bool truncated) = MatchingLinesExporter.BuildClipboardText(src, maxChars: 6);

            Assert.True(truncated);
            Assert.True(count >= 1 && count < 4);
            Assert.StartsWith("aaaa", text);
        }

        [Fact]
        public void Write_StreamsEveryLineWithEndings()
        {
            var src = new FakeLinesWithEndings(
                new[] { "one", "two", "three" },
                new[] { LineEnding.CrLf, LineEnding.Lf, LineEnding.None });

            var writer = new StringWriter();
            MatchingLinesExporter.Write(src, writer);

            Assert.Equal("one\r\ntwo\nthree", writer.ToString());
        }

        [Fact]
        public void Write_Empty_WritesNothing()
        {
            var writer = new StringWriter();
            MatchingLinesExporter.Write(new FakeLines(), writer);
            Assert.Equal(string.Empty, writer.ToString());
        }

        [Fact]
        public void Write_NoEndingSource_SeparatesWithLf()
        {
            var writer = new StringWriter();
            MatchingLinesExporter.Write(new FakeLines("x", "y"), writer);
            Assert.Equal("x\ny", writer.ToString());
        }
    }
}
