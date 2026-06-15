using System.Text.RegularExpressions;

namespace FujiyNotepad.WinUI.Logic
{
    /// <summary>
    /// Finds the spans to highlight within a single decoded line of text. Used by the view to paint every
    /// occurrence of the current Find term in the viewport (not just the one selected match). Implementations
    /// match the corresponding Find engine's semantics so the highlights agree with "find next/previous".
    /// </summary>
    public interface ILineHighlighter
    {
        /// <summary>
        /// The non-overlapping match spans (character start, length) within <paramref name="line"/>, in
        /// increasing order. Returns an empty list when there is no match.
        /// </summary>
        IReadOnlyList<(int Start, int Length)> Find(string line);
    }

    /// <summary>
    /// Literal-term highlighter mirroring <c>TextSearcher</c>'s byte search at the character level:
    /// non-overlapping matches, optional ASCII case-insensitivity (A-Z folded to a-z only), and an optional
    /// whole-word filter using the same word-character set (<c>[A-Za-z0-9_]</c>).
    /// </summary>
    public sealed class LiteralLineHighlighter : ILineHighlighter
    {
        private static readonly IReadOnlyList<(int, int)> None = Array.Empty<(int, int)>();

        private readonly string term;
        private readonly bool ignoreCase;
        private readonly bool wholeWord;

        public LiteralLineHighlighter(string term, bool ignoreCase, bool wholeWord)
        {
            this.term = term;
            this.ignoreCase = ignoreCase;
            this.wholeWord = wholeWord;
        }

        public IReadOnlyList<(int Start, int Length)> Find(string line)
        {
            if (term.Length == 0 || line.Length < term.Length)
            {
                return None;
            }

            List<(int, int)>? result = null;
            int from = 0;
            int last = line.Length - term.Length;
            while (from <= last)
            {
                int idx = IndexOf(line, from);
                if (idx < 0)
                {
                    break;
                }

                if (!wholeWord || IsWholeWord(line, idx, term.Length))
                {
                    (result ??= new List<(int, int)>()).Add((idx, term.Length));
                    from = idx + term.Length; // non-overlapping
                }
                else
                {
                    from = idx + 1;
                }
            }

            return (IReadOnlyList<(int, int)>?)result ?? None;
        }

        private int IndexOf(string line, int start)
        {
            int last = line.Length - term.Length;
            for (int i = start; i <= last; i++)
            {
                int j = 0;
                for (; j < term.Length; j++)
                {
                    char a = line[i + j];
                    char b = term[j];
                    if (ignoreCase)
                    {
                        a = AsciiLower(a);
                        b = AsciiLower(b);
                    }
                    if (a != b)
                    {
                        break;
                    }
                }
                if (j == term.Length)
                {
                    return i;
                }
            }
            return -1;
        }

        private static char AsciiLower(char c) => c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;

        private static bool IsWordChar(char c)
            => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';

        private static bool IsWholeWord(string line, int start, int length)
        {
            bool leftBoundary = start == 0 || !IsWordChar(line[start - 1]);
            bool rightBoundary = start + length >= line.Length || !IsWordChar(line[start + length]);
            return leftBoundary && rightBoundary;
        }
    }

    /// <summary>
    /// Regex highlighter mirroring <c>RegexLineSearcher</c>: every non-empty (non-overlapping) match of the
    /// supplied <see cref="Regex"/> on the line. The regex already carries the case/whole-word options.
    /// </summary>
    public sealed class RegexLineHighlighter : ILineHighlighter
    {
        private static readonly IReadOnlyList<(int, int)> None = Array.Empty<(int, int)>();

        private readonly Regex regex;

        public RegexLineHighlighter(Regex regex) => this.regex = regex;

        public IReadOnlyList<(int Start, int Length)> Find(string line)
        {
            List<(int, int)>? result = null;
            foreach (Match m in regex.Matches(line))
            {
                if (m.Length > 0)
                {
                    (result ??= new List<(int, int)>()).Add((m.Index, m.Length));
                }
            }
            return (IReadOnlyList<(int, int)>?)result ?? None;
        }
    }
}
