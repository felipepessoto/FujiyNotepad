namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// One persistent highlight rule: a pattern (literal or regex) plus the colour its matches are painted in.
    /// These are parsed from the user's editable rules text (see <see cref="HighlightRuleText"/>) and compiled
    /// into a <see cref="HighlightRuleSet"/>; they are not themselves serialized (the raw text is what persists).
    /// </summary>
    public sealed class HighlightRule
    {
        /// <summary>The literal substring or regular expression to match within each line.</summary>
        public string Pattern { get; set; } = "";

        /// <summary>True to treat <see cref="Pattern"/> as a regular expression; false for a literal substring.</summary>
        public bool IsRegex { get; set; }

        /// <summary>True to match case-sensitively; false (default) folds ASCII case like the Find bar.</summary>
        public bool MatchCase { get; set; }

        /// <summary>
        /// The colour token (a name like <c>red</c> or a hex value like <c>#FFD54F</c> / <c>#80FF0000</c>) the
        /// matches are painted in. Resolved to an ARGB value by <see cref="HighlightColors"/> when compiled.
        /// </summary>
        public string Color { get; set; } = HighlightColors.DefaultColorName;
    }
}
