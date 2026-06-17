namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Formats a <see cref="TimeSpan"/> as a compact, human-readable duration for the status bar — e.g.
    /// <c>250ms</c>, <c>1.500s</c>, <c>1m 30s</c>, <c>1h 1m 1s</c>, <c>2d</c>. Sub-second precision is shown
    /// only for durations under a minute. The magnitude is used, so a negative span formats like its absolute
    /// value. (Named <c>DurationFormatter</c> to avoid colliding with <c>Microsoft.UI.Xaml.Duration</c>.)
    /// </summary>
    public static class DurationFormatter
    {
        public static string Format(TimeSpan span)
        {
            TimeSpan d = span.Duration();

            if (d.TotalMilliseconds < 1000)
            {
                return $"{(int)Math.Round(d.TotalMilliseconds)}ms";
            }

            var parts = new List<string>(4);
            if (d.Days > 0)
            {
                parts.Add($"{d.Days}d");
            }
            if (d.Hours > 0)
            {
                parts.Add($"{d.Hours}h");
            }
            if (d.Minutes > 0)
            {
                parts.Add($"{d.Minutes}m");
            }

            bool subMinute = d.Days == 0 && d.Hours == 0 && d.Minutes == 0;
            if (subMinute && d.Milliseconds > 0)
            {
                parts.Add($"{d.Seconds}.{d.Milliseconds:000}s");
            }
            else if (d.Seconds > 0 || parts.Count == 0)
            {
                parts.Add($"{d.Seconds}s");
            }

            return string.Join(" ", parts);
        }
    }
}
