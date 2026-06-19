using System.Globalization;

namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Builds the status-bar strings — cursor position with the selection summary, line and character counts,
    /// and the filter / indexing progress text. Centralizing them here keeps the number formatting and the
    /// single-line / multi-line / uncounted selection branches in one device-free, unit-tested place instead
    /// of scattered across the window code.
    /// </summary>
    public static class StatusText
    {
        /// <summary>
        /// The cursor readout: <c>"Ln {line}, Col {col}"</c> (both 1-based here from 0-based inputs), followed
        /// by a selection summary when something is selected, and a <c>"delta = …"</c> log-duration when a
        /// multi-line selection's endpoints both carry a timestamp. Mirrors the previous inline formatting.
        /// </summary>
        public static string CursorStatus(int displayLine, int column, SelectionStats selection, TimeSpan? timestampDelta)
        {
            string text = $"Ln {displayLine + 1}, Col {column + 1}";

            if (selection.Lines == 1)
            {
                text += $"  ({selection.Characters:N0} selected)";
            }
            else if (selection.Lines > 1)
            {
                text += selection.Characters >= 0
                    ? $"  ({selection.Characters:N0} selected, {selection.Lines:N0} lines)"
                    : $"  ({selection.Lines:N0} lines selected)";

                // If the first and last selected lines both begin with a timestamp, show how long the span
                // covers -- quick triage of how long something took in a log (issue #67).
                if (timestampDelta is { } delta)
                {
                    text += $"  delta = {DurationFormatter.Format(delta)}";
                }
            }

            return text;
        }

        /// <summary>A line total, e.g. <c>"12,345 lines"</c>.</summary>
        public static string LineCount(long lines) => $"{lines:N0} lines";

        /// <summary>The filter view's match summary, e.g. <c>"Filtered: 12 of 1,000 lines"</c>.</summary>
        public static string Filtered(long matches, long total) => $"Filtered: {matches:N0} of {total:N0} lines";

        /// <summary>Background-indexing progress, e.g. <c>"50% indexed"</c>.</summary>
        public static string IndexProgress(int percent) => $"{percent}% indexed";

        /// <summary>
        /// The character total: <c>""</c> for a negative (unavailable) count, <c>"1 character"</c> for exactly
        /// one, otherwise <c>"{count} characters"</c>.
        /// </summary>
        public static string CharacterCount(long count) =>
            count < 0 ? string.Empty : count == 1 ? "1 character" : $"{count:N0} characters";

        /// <summary>
        /// A human-readable byte size, e.g. <c>"512 bytes"</c>, <c>"2.5 KB"</c>, <c>"340.0 MB"</c>,
        /// <c>"2.50 GB"</c> (1024-based units). Shown in place of the character count for very large files,
        /// whose exact count is deferred to an on-demand action (issue #39).
        /// </summary>
        public static string FileSize(long bytes)
        {
            if (bytes < 0)
            {
                return string.Empty;
            }

            const long Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
            return bytes switch
            {
                1 => "1 byte",
                < Kb => $"{bytes:N0} bytes",
                < Mb => $"{bytes / (double)Kb:N1} KB",
                < Gb => $"{bytes / (double)Mb:N1} MB",
                _ => $"{bytes / (double)Gb:N2} GB",
            };
        }
    }
}
