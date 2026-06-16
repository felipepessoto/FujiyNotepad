using System.Text.RegularExpressions;

namespace FujiyNotepad.Presentation
{
    /// <summary>A coloured match span within a line: character <see cref="Start"/>/<see cref="Length"/> plus the
    /// packed 0xAARRGGBB colour of the rule that produced it.</summary>
    public readonly record struct HighlightSpan(int Start, int Length, uint Argb);

    /// <summary>
    /// A compiled set of persistent highlight rules. Each rule becomes a per-line <see cref="ILineHighlighter"/>
    /// (the same engines that back Find) paired with its colour, so the canvas paints every match across the
    /// whole file. Invalid rules (blank pattern, bad regex) are dropped at build time; this stays UI-free so the
    /// whole thing is unit-testable.
    /// </summary>
    public sealed class HighlightRuleSet
    {
        private static readonly IReadOnlyList<HighlightSpan> None = Array.Empty<HighlightSpan>();

        private readonly (ILineHighlighter Highlighter, uint Argb)[] rules;

        private HighlightRuleSet((ILineHighlighter, uint)[] rules) => this.rules = rules;

        /// <summary>Number of rules that compiled successfully.</summary>
        public int Count => rules.Length;

        /// <summary>
        /// Compiles the given rules, skipping any with an empty pattern or an invalid regex. An unrecognized
        /// colour token falls back to the default colour rather than dropping the rule.
        /// </summary>
        public static HighlightRuleSet Build(IEnumerable<HighlightRule> source)
        {
            var compiled = new List<(ILineHighlighter, uint)>();
            foreach (HighlightRule rule in source)
            {
                if (rule is null || string.IsNullOrEmpty(rule.Pattern))
                {
                    continue;
                }

                if (!HighlightColors.TryParse(rule.Color, out uint argb))
                {
                    HighlightColors.TryParse(HighlightColors.DefaultColorName, out argb);
                }

                ILineHighlighter? highlighter = CreateHighlighter(rule);
                if (highlighter is not null)
                {
                    compiled.Add((highlighter, argb));
                }
            }

            return new HighlightRuleSet(compiled.ToArray());
        }

        private static ILineHighlighter? CreateHighlighter(HighlightRule rule)
        {
            if (rule.IsRegex)
            {
                try
                {
                    RegexOptions options = rule.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                    return new RegexLineHighlighter(new Regex(rule.Pattern, options));
                }
                catch (ArgumentException)
                {
                    return null; // invalid regex -> drop this rule
                }
            }

            return new LiteralLineHighlighter(rule.Pattern, ignoreCase: !rule.MatchCase, wholeWord: false);
        }

        /// <summary>
        /// All coloured match spans on <paramref name="line"/>, gathered rule by rule (so when two rules overlap
        /// a character, the later rule's colour is painted on top). Returns an empty list when nothing matches.
        /// </summary>
        public IReadOnlyList<HighlightSpan> Find(string line)
        {
            if (rules.Length == 0)
            {
                return None;
            }

            List<HighlightSpan>? result = null;
            foreach ((ILineHighlighter highlighter, uint argb) in rules)
            {
                IReadOnlyList<(int Start, int Length)> spans = highlighter.Find(line);
                for (int i = 0; i < spans.Count; i++)
                {
                    (result ??= new List<HighlightSpan>()).Add(new HighlightSpan(spans[i].Start, spans[i].Length, argb));
                }
            }

            return (IReadOnlyList<HighlightSpan>?)result ?? None;
        }
    }
}
