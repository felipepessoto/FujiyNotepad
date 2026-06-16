using FujiyNotepad.Core;

namespace FujiyNotepad.TestSupport
{
    /// <summary>
    /// A trivial in-memory <see cref="ILineSource"/> over a fixed set of lines, for tests that operate on
    /// decoded line text without a real file (filtering, searching, exporting, ...).
    /// </summary>
    public sealed class FakeLines : ILineSource
    {
        private readonly string[] lines;
        public FakeLines(params string[] lines) => this.lines = lines;
        public int LineCount => lines.Length;
        public string GetLine(int lineIndex) => lines[lineIndex];
    }

    /// <summary>
    /// A <see cref="FakeLines"/> that also reports a per-line terminator via <see cref="ILineEndingSource"/>,
    /// for tests that need to assert line-ending handling (e.g. CRLF/LF preservation on export).
    /// </summary>
    public sealed class FakeLinesWithEndings : ILineSource, ILineEndingSource
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
}
