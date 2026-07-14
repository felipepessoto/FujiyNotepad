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
        private readonly Func<string, string?, string?, bool> write;
        // Keyed by context, which now embeds a Windows file path (e.g. "FileWatcher: C:\a.log"), so compare keys
        // case-insensitively — matching the codebase's path convention (RecentFiles) — so the same file opened
        // with different casing de-dupes as one file rather than logging twice.
        private readonly Dictionary<string, string> lastSignature = new(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new();

        /// <summary>
        /// Creates a log that appends via <paramref name="write"/> — <c>(type, message, stackTrace) => written</c>.
        /// The delegate must be best-effort (never throw and return <c>false</c> on failure); the seam also keeps
        /// the type unit-testable without touching the disk.
        /// </summary>
        public DiagnosticLog(Func<string, string?, string?, bool> write) => this.write = write;

        /// <summary>Creates a log whose entries are appended by <paramref name="sink"/>.</summary>
        public DiagnosticLog(CrashLogger sink) : this(sink.Write) { }

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
        /// unless the previous <em>written</em> report for that same context was identical (same exception type and
        /// message) — so a failure that recurs every tick is written once rather than every tick. Returns
        /// <c>true</c> only when an entry was actually written; a <c>null</c> exception, a null/blank context, or a
        /// failed write returns <c>false</c> (and does not suppress the next identical attempt). Never throws.
        /// </summary>
        public bool LogSwallowed(string context, Exception? error)
        {
            if (error is null || string.IsNullOrWhiteSpace(context))
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

                // Write while holding the lock (so concurrent callers can't interleave their file appends) and
                // remember the signature only after the write actually succeeds — a transient write failure must
                // not cache the signature and thereby suppress the next identical attempt.
                if (!write(context, signature, error.StackTrace))
                {
                    return false;
                }
                lastSignature[context] = signature;
                return true;
            }
        }
    }
}
