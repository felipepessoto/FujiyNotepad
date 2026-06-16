using System;
using System.Text.RegularExpressions;

namespace FujiyNotepad.Core
{
    /// <summary>
    /// Line-scoped regular-expression search over an <see cref="ILineSource"/>. Each line's decoded text is
    /// matched independently, so <c>.</c> never crosses a newline and a match can't straddle the chunked
    /// byte reader — a good fit for the line-indexed viewer. Only currently-available lines are searched.
    /// Case-insensitivity and whole-word are expressed in the supplied <see cref="Regex"/> (via
    /// <see cref="RegexOptions.IgnoreCase"/> and <c>\b</c> anchors), so this type stays option-agnostic.
    /// Empty (zero-length) matches are skipped so Find advances and the counter only counts real text.
    /// </summary>
    public sealed class RegexLineSearcher
    {
        private readonly ILineSource lines;

        public RegexLineSearcher(ILineSource lines) => this.lines = lines;

        public readonly record struct LineMatch(int LineIndex, int CharStart, int CharLength);

        /// <summary>
        /// Returns the first non-empty match at or after (<paramref name="startLine"/>,
        /// <paramref name="startChar"/>), scanning forward through the available lines, or <c>null</c>.
        /// </summary>
        public LineMatch? FindNext(Regex regex, int startLine, int startChar, CancellationToken token = default)
        {
            int count = lines.LineCount;
            for (int line = Math.Max(0, startLine); line < count; line++)
            {
                if (token.IsCancellationRequested)
                {
                    return null;
                }

                string text = lines.GetLine(line);
                int from = line == startLine ? Math.Clamp(startChar, 0, text.Length) : 0;
                Match m = regex.Match(text, from);
                while (m.Success)
                {
                    if (m.Length > 0)
                    {
                        return new LineMatch(line, m.Index, m.Length);
                    }
                    // Zero-length match (e.g. "a*", "^"): step one character so we don't stall.
                    int next = m.Index + 1;
                    if (next > text.Length)
                    {
                        break;
                    }
                    m = regex.Match(text, next);
                }
            }
            return null;
        }

        /// <summary>
        /// Returns the last non-empty match strictly before (<paramref name="startLine"/>,
        /// <paramref name="startChar"/>), scanning backward through the available lines, or <c>null</c>.
        /// On the start line only matches whose start column is below <paramref name="startChar"/> qualify;
        /// the per-line canonical (non-overlapping, forward) match set is reused, so this is the exact inverse
        /// of <see cref="FindNext"/>.
        /// </summary>
        public LineMatch? FindPrevious(Regex regex, int startLine, int startChar, CancellationToken token = default)
        {
            int count = lines.LineCount;
            for (int line = Math.Min(startLine, count - 1); line >= 0; line--)
            {
                if (token.IsCancellationRequested)
                {
                    return null;
                }

                string text = lines.GetLine(line);
                int limit = line == startLine ? Math.Clamp(startChar, 0, text.Length) : int.MaxValue;

                LineMatch? best = null;
                foreach (Match m in regex.Matches(text))
                {
                    if (m.Index >= limit)
                    {
                        break; // matches come in increasing-index order; nothing further qualifies
                    }
                    if (m.Length > 0)
                    {
                        best = new LineMatch(line, m.Index, m.Length);
                    }
                }

                if (best is not null)
                {
                    return best;
                }
            }
            return null;
        }

        /// <summary>
        /// Counts every non-empty match across all available lines. Cancellation stops early and returns the
        /// partial count; progress is reported as a 0-100 percentage of lines scanned.
        /// </summary>
        public int CountAll(Regex regex, IProgress<int>? progress = null, CancellationToken token = default, Action<int>? onLineWithMatch = null)
        {
            int count = lines.LineCount;
            int total = 0;
            int lastPercent = -1;
            progress?.Report(0);

            for (int line = 0; line < count; line++)
            {
                if (token.IsCancellationRequested)
                {
                    return total;
                }

                int before = total;
                foreach (Match m in regex.Matches(lines.GetLine(line)))
                {
                    if (m.Length > 0)
                    {
                        total++;
                    }
                }

                if (onLineWithMatch != null && total > before)
                {
                    onLineWithMatch(line);
                }

                if (progress != null && count > 0)
                {
                    int percent = (int)((line + 1L) * 100 / count);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(percent);
                    }
                }
            }

            progress?.Report(100);
            return total;
        }
    }
}
