namespace FujiyNotepad.Presentation
{
    /// <summary>Log severity floors, ordered from least to most severe.</summary>
    public enum LogSeverity
    {
        Trace,
        Debug,
        Info,
        Warn,
        Error,
        Fatal,
    }

    /// <summary>Severity presets for the existing per-line regex filter, independent of any log format.</summary>
    public static class SeverityFilter
    {
        private static readonly string[] levelPatterns =
        {
            "TRACE|VERBOSE",
            "DEBUG",
            "INFO|NOTICE",
            "WARN|WARNING",
            "ERROR|ERR|SEVERE",
            "FATAL|CRITICAL",
        };

        /// <summary>
        /// Matches whole severity tokens at or above the floor, anywhere on a line, ignoring case.
        /// Lines without a severity token (including stack-trace continuations) do not match.
        /// </summary>
        public static string GetPattern(LogSeverity minimum)
        {
            int index = (int)minimum;
            if (index < 0 || index >= levelPatterns.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(minimum));
            }

            return @"(?i)\b(?:" + string.Join("|", levelPatterns, index, levelPatterns.Length - index) + @")\b";
        }
    }
}
