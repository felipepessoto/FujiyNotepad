using System.Globalization;
using System.Text.RegularExpressions;

namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Parses the timestamp at the start of a (log) line into a <see cref="DateTimeOffset"/>. Recognizes, in
    /// order: an ISO 8601 / <c>yyyy-MM-dd HH:mm:ss[.fff]</c> date-time (optional <c>T</c> separator, fractional
    /// seconds with a <c>.</c> or <c>,</c> separator — log4j / Python logging use a comma — and an optional
    /// timezone), a syslog <c>MMM d HH:mm:ss</c> (no year — the current year is assumed), and a
    /// bare <c>HH:mm:ss[.fff]</c> time; any of these may also be wrapped in <c>[brackets]</c>. A timestamp with
    /// no timezone is read as UTC and a bare time/syslog value uses a synthetic date — but since both endpoints
    /// of a selection are parsed the same way, the delta between them is correct (except across a year boundary
    /// for year-less syslog, or across midnight for a bare time). Pure and unit-testable; uses interpreted Regex
    /// so it stays Native-AOT safe.
    /// </summary>
    public static class TimestampParser
    {
        // 2024-01-02T15:04:05(.123|,123)?(Z|+02:00)?  — the 'T' may be a space, the fractional separator a '.'
        // or ',' (log4j / Python logging use a comma), and the fraction and timezone are optional.
        private static readonly Regex DateTimePattern = new(
            @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}([.,]\d+)?(Z|[+-]\d{2}:?\d{2})?",
            RegexOptions.CultureInvariant);

        // Syslog: "Jan  2 15:04:05" (one or two spaces before a 1- or 2-digit day; no year).
        private static readonly Regex SyslogPattern = new(
            @"^(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) +\d{1,2} +\d{2}:\d{2}:\d{2}",
            RegexOptions.CultureInvariant);

        // A bare time: "15:04:05" with an optional '.'- or ','-separated fraction.
        private static readonly Regex TimePattern = new(
            @"^\d{2}:\d{2}:\d{2}([.,]\d+)?",
            RegexOptions.CultureInvariant);

        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

        // A fixed reference date for bare-time values; the date cancels out in a same-day delta.
        private static readonly DateTimeOffset TimeOnlyEpoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// Tries to parse the timestamp at the start of <paramref name="line"/>. Returns <c>false</c> when the
        /// line (after optional leading whitespace and one level of <c>[brackets]</c>) does not begin with a
        /// recognized timestamp.
        /// </summary>
        public static bool TryParseLeading(string? line, out DateTimeOffset value)
        {
            value = default;
            if (string.IsNullOrEmpty(line))
            {
                return false;
            }

            string s = line.TrimStart();

            // Unwrap one level of [brackets], e.g. "[2024-01-02 15:04:05] message" or "[15:04:05]".
            if (s.Length > 0 && s[0] == '[')
            {
                int close = s.IndexOf(']');
                if (close < 0)
                {
                    return false;
                }
                s = s.Substring(1, close - 1).Trim();
            }

            Match m = DateTimePattern.Match(s);
            if (m.Success && DateTimeOffset.TryParse(
                    NormalizeFraction(m.Value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                    out value))
            {
                return true;
            }

            m = SyslogPattern.Match(s);
            if (m.Success)
            {
                string normalized = Whitespace.Replace(m.Value, " ");
                if (DateTime.TryParseExact(
                        normalized, "MMM d HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None,
                        out DateTime dt))
                {
                    value = new DateTimeOffset(dt, TimeSpan.Zero);
                    return true;
                }
            }

            m = TimePattern.Match(s);
            if (m.Success && TimeSpan.TryParse(NormalizeFraction(m.Value), CultureInfo.InvariantCulture, out TimeSpan ts))
            {
                value = TimeOnlyEpoch + ts;
                return true;
            }

            return false;
        }

        // The invariant date/time parsers expect a '.' before fractional seconds; a log timestamp that uses a
        // ',' (the ISO 8601 / log4j / Python-logging style, e.g. "22:05:51,119") is normalized here. The matched
        // timestamp span contains no other comma, so a plain replace is safe.
        private static string NormalizeFraction(string matched) => matched.Replace(',', '.');
    }
}
