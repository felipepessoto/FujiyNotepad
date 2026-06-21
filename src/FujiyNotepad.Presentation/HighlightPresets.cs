namespace FujiyNotepad.Presentation
{
    /// <summary>A named, ready-to-apply set of highlight rules in the <see cref="HighlightRuleText"/> format.</summary>
    public sealed record HighlightPreset(string Name, string RulesText);

    /// <summary>
    /// Built-in highlight-rule presets the user can insert from the Highlight Rules dialog and then tweak. They
    /// are pure data over the existing <see cref="HighlightRuleText"/> format (one <c>color[/flags] pattern</c>
    /// per line, <c>#</c> lines are comments), so they stay Native-AOT safe and unit-testable. Patterns use the
    /// interpreted regex engine, matching the runtime-supplied highlight rules.
    /// </summary>
    public static class HighlightPresets
    {
        /// <summary>The presets, in display order.</summary>
        public static IReadOnlyList<HighlightPreset> All { get; } = new[]
        {
            new HighlightPreset("Log levels",
                "# Log severity levels (case-insensitive)\n" +
                "red/regex \\b(ERROR|ERR|FATAL|CRITICAL|SEVERE|EXCEPTION)\\b\n" +
                "orange/regex \\b(WARN|WARNING)\\b\n" +
                "blue/regex \\b(INFO|NOTICE)\\b\n" +
                "green/regex \\b(SUCCESS|PASSED|OK)\\b\n" +
                "gray/regex \\b(DEBUG|TRACE|VERBOSE)\\b\n"),

            new HighlightPreset("Web access log",
                "# HTTP methods and status codes (Apache / nginx access logs)\n" +
                "blue/regex \\b(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\\b\n" +
                "green/regex \\b2\\d\\d\\b\n" +
                "amber/regex \\b3\\d\\d\\b\n" +
                "red/regex \\b[45]\\d\\d\\b\n"),

            new HighlightPreset("JSON",
                "# JSON keys, literals and numbers\n" +
                "purple/regex \"[^\"]*\"\\s*:\n" +
                "orange/regex \\b(true|false|null)\\b\n" +
                "teal/regex \\b\\d+(\\.\\d+)?\\b\n"),

            new HighlightPreset("Timestamps & IDs",
                "# ISO-8601 timestamps, GUIDs and IPv4 addresses\n" +
                "gray/regex \\b\\d{4}-\\d{2}-\\d{2}[ T]\\d{2}:\\d{2}:\\d{2}\n" +
                "teal/regex \\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\b\n" +
                "blue/regex \\b\\d{1,3}(\\.\\d{1,3}){3}\\b\n"),

            new HighlightPreset("Spark / YARN",
                "# Apache Spark driver / executor logs: emphasise scheduler & lifecycle events, dim token/SAS noise\n" +
                "red/regex (Lost executor|ExecutorLostFailure|FetchFailed|TaskKilled|Container .* exited|OutOfMemory)\n" +
                "orange/regex (Removing executor|\\bWARN\\b|Stage .* failed)\n" +
                "green/regex (Added executor|Job \\d+ finished|took [\\d.]+ s\\b)\n" +
                "blue/regex (DAGScheduler|TaskSetManager|Submitting .*Stage|YarnAllocator)\n" +
                "gray/regex (TokenLibrary|SystemSASProviderWithRefresh|SasUtils|AsyncAppender)\n"),
        };
    }
}
