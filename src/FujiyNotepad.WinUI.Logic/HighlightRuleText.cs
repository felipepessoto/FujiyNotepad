using System.Text;

namespace FujiyNotepad.WinUI.Logic
{
    /// <summary>
    /// Parses (and formats) the user's editable highlight-rules text. One rule per line:
    /// <c>color[/flags] pattern</c> — the colour token comes first (it never contains spaces), optionally
    /// followed by <c>/regex</c> and/or <c>/case</c> flags, then a space, then the pattern as the rest of the
    /// line. Putting the free-form pattern last keeps regex metacharacters (including <c>|</c>) intact. Blank
    /// lines and lines starting with <c>#</c> are ignored, so the text doubles as its own documentation.
    /// </summary>
    public static class HighlightRuleText
    {
        /// <summary>A commented template shown when the user has no rules yet.</summary>
        public const string DefaultExample =
            "# Highlight rules - one per line:  color[/flags] pattern\n" +
            "# color: a name (red, orange, amber, yellow, green, teal, blue, cyan, purple, pink, gray)\n" +
            "#        or hex #RGB / #RRGGBB / #AARRGGBB\n" +
            "# flags: /regex and/or /case   e.g.  blue/regex,case  Error\\d+\n" +
            "# Lines starting with # are ignored.\n" +
            "red ERROR\n" +
            "orange WARN\n";

        /// <summary>Parses the rules text into rules, in order, skipping blank/comment/incomplete lines.</summary>
        public static List<HighlightRule> Parse(string? text)
        {
            var rules = new List<HighlightRule>();
            if (string.IsNullOrEmpty(text))
            {
                return rules;
            }

            // Split on CR and/or LF: WinUI's multiline TextBox.Text uses lone '\r' as its line separator, while
            // files/settings use '\n' or '\r\n'. Splitting on both makes every source parse the same (empty
            // pieces from a '\r\n' pair are dropped by the blank-line check below).
            foreach (string rawLine in text.Split('\r', '\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                // A '#' line is a comment unless it is a "#RRGGBB pattern" rule (hex colour first), so both
                // "# note" comments and hex-coloured rules coexist.
                if (line[0] == '#' && !StartsWithColorToken(line))
                {
                    continue;
                }

                int split = IndexOfWhitespace(line);
                if (split < 0)
                {
                    continue; // a metadata token with no pattern after it
                }

                string meta = line.Substring(0, split);
                string pattern = line.Substring(split).Trim();
                if (pattern.Length == 0)
                {
                    continue;
                }

                var rule = new HighlightRule { Pattern = pattern };

                int slash = meta.IndexOf('/');
                string color = slash < 0 ? meta : meta.Substring(0, slash);
                if (color.Length > 0)
                {
                    rule.Color = color;
                }

                if (slash >= 0)
                {
                    foreach (string flag in meta.Substring(slash + 1).Split(','))
                    {
                        string f = flag.Trim();
                        if (f.Equals("regex", StringComparison.OrdinalIgnoreCase))
                        {
                            rule.IsRegex = true;
                        }
                        else if (f.Equals("case", StringComparison.OrdinalIgnoreCase))
                        {
                            rule.MatchCase = true;
                        }
                    }
                }

                rules.Add(rule);
            }

            return rules;
        }

        /// <summary>Formats rules back into the canonical one-per-line text (the inverse of <see cref="Parse"/>).</summary>
        public static string Format(IEnumerable<HighlightRule> rules)
        {
            var sb = new StringBuilder();
            foreach (HighlightRule rule in rules)
            {
                if (rule is null || string.IsNullOrEmpty(rule.Pattern))
                {
                    continue;
                }

                sb.Append(rule.Color);
                if (rule.IsRegex || rule.MatchCase)
                {
                    sb.Append('/');
                    if (rule.IsRegex)
                    {
                        sb.Append("regex");
                        if (rule.MatchCase)
                        {
                            sb.Append(',');
                        }
                    }
                    if (rule.MatchCase)
                    {
                        sb.Append("case");
                    }
                }

                sb.Append(' ').Append(rule.Pattern).Append('\n');
            }

            return sb.ToString();
        }

        private static int IndexOfWhitespace(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsWhiteSpace(s[i]))
                {
                    return i;
                }
            }
            return -1;
        }

        // True when the line's first token (before any '/flags' and the pattern) is a recognized colour, i.e. the
        // '#'-prefixed line is a hex-coloured rule rather than a comment.
        private static bool StartsWithColorToken(string line)
        {
            int ws = IndexOfWhitespace(line);
            if (ws < 0)
            {
                return false;
            }

            string meta = line.Substring(0, ws);
            int slash = meta.IndexOf('/');
            string color = slash < 0 ? meta : meta.Substring(0, slash);
            return HighlightColors.TryParse(color, out _);
        }
    }
}
