using System.Collections.Generic;

namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Records deliberately-swallowed, non-fatal exceptions — a file-watcher that won't start, a tail read that
    /// fails, an indexing teardown fault — so an otherwise-invisible recurring failure leaves an actionable trail
    /// (issue #143). Reports are de-duplicated per context: a failure that recurs every timer tick is logged once
    /// (until a different failure appears for that context), so a persistently-failing site can't spam the log.
    /// Best-effort and never throws — a diagnostic write must not itself disrupt the app. The actual append reuses
    /// <see cref="CrashLogger"/> but targets a separate <c>diagnostics.log</c>, so <c>crash.log</c> stays reserved
    /// for real crashes. Device-free, so it unit-tests without a WinUI host.
    /// </summary>
    public sealed class DiagnosticLog
    {
        private readonly CrashLogger sink;
        private readonly Dictionary<string, string> lastSignature = new();
        private readonly object gate = new();

        public DiagnosticLog(CrashLogger sink) => this.sink = sink;

        /// <summary>The default diagnostics log at <c>%LOCALAPPDATA%\FujiyNotepad\diagnostics.log</c>.</summary>
        public static DiagnosticLog Default()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FujiyNotepad");
            return new DiagnosticLog(new CrashLogger(Path.Combine(dir, "diagnostics.log")));
        }

        /// <summary>
        /// Records <paramref name="error"/> under the <paramref name="context"/> label (e.g. "FileWatcher"),
        /// unless the previous report for that same context was identical (same exception type and message) — so a
        /// failure that recurs every tick is written once rather than every tick. Returns <c>true</c> when an entry
        /// was written; a <c>null</c> exception (or a swallowed I/O failure in the sink) returns <c>false</c>.
        /// </summary>
        public bool LogSwallowed(string context, Exception? error)
        {
            if (error is null)
            {
                return false;
            }

            string signature = (error.GetType().FullName ?? nameof(Exception)) + ": " + error.Message;
            lock (gate)
            {
                if (lastSignature.TryGetValue(context, out string? previous) && previous == signature)
                {
                    return false; // same failure already reported for this context; don't repeat it every tick
                }
                lastSignature[context] = signature;
            }

            return sink.Write(context, signature, error.StackTrace);
        }
    }
}
