using System.Text;

namespace FujiyNotepad.Core
{
    /// <summary>
    /// Extracts the lines of an <see cref="ILineSource"/> as text for the "export / copy matching lines"
    /// commands (the GUI equivalent of <c>grep PATTERN file &gt; out.txt</c>). Over a
    /// <see cref="FilteredLineSource"/> that source is exactly the set of matching lines, so this just walks
    /// <c>[0, LineCount)</c>. Each line keeps its original terminator (forwarded through
    /// <see cref="ILineEndingSource"/>) so the output is faithful to the file, and the file's final
    /// unterminated line stays unterminated. Pure and dependency-free, so it is headlessly unit-testable;
    /// the clipboard and the file picker are the caller's (desktop) concern.
    /// </summary>
    public static class MatchingLinesExporter
    {
        /// <summary>
        /// Default cap on characters placed on the clipboard, so copying a huge match set can't allocate an
        /// unbounded string on the UI thread (saving to a file is streamed and uncapped instead).
        /// </summary>
        public const int DefaultClipboardCharCap = 8 * 1024 * 1024;

        /// <summary>
        /// Builds the clipboard text for the lines of <paramref name="source"/>, stopping once
        /// <paramref name="maxChars"/> characters have been gathered. Returns the assembled text, the number
        /// of whole lines included, and whether the result was truncated by the cap.
        /// </summary>
        public static (string Text, int LineCount, bool Truncated) BuildClipboardText(
            ILineSource source, int maxChars = DefaultClipboardCharCap)
        {
            int count = source.LineCount;
            var sb = new StringBuilder();
            int written = 0;

            for (int i = 0; i < count; i++)
            {
                if (sb.Length >= maxChars)
                {
                    return (sb.ToString(), written, true);
                }
                sb.Append(source.GetLine(i)).Append(EndingFor(source, i, count));
                written++;
            }

            return (sb.ToString(), written, false);
        }

        /// <summary>
        /// Writes every line of <paramref name="source"/> to <paramref name="writer"/> (uncapped, streamed),
        /// for "Save Matching Lines As...". The writer's encoding determines the output bytes.
        /// </summary>
        public static void Write(ILineSource source, TextWriter writer)
        {
            int count = source.LineCount;
            for (int i = 0; i < count; i++)
            {
                writer.Write(source.GetLine(i));
                writer.Write(EndingFor(source, i, count));
            }
        }

        // The terminator to append after line `index`: the line's own ending when the source reports one,
        // otherwise a '\n' to keep lines separated — except after the very last line, which gets nothing
        // (so a file whose final line had no newline does not gain a trailing one).
        private static string EndingFor(ILineSource source, int index, int count)
        {
            if (source is ILineEndingSource endings)
            {
                switch (endings.GetLineEnding(index))
                {
                    case LineEnding.CrLf:
                        return "\r\n";
                    case LineEnding.Lf:
                        return "\n";
                }
            }
            return index < count - 1 ? "\n" : string.Empty;
        }
    }
}
