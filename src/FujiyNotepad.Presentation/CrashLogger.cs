using System.Globalization;
using System.Text;

namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Appends crash diagnostics (timestamp, exception type, message, stack trace) to a log file so an
    /// otherwise-silent Native AOT crash leaves an actionable trail beside <c>settings.json</c>. All I/O is
    /// best-effort and never throws: it runs from the unhandled-exception handler, where a second failure
    /// must not mask the original crash. AOT-safe — plain string formatting and a file append, no reflection.
    /// The logging logic lives in this device-free layer so it is unit-testable without a WinUI host.
    /// </summary>
    public sealed class CrashLogger
    {
        private readonly string filePath;

        public CrashLogger(string filePath) => this.filePath = filePath;

        /// <summary>The default log at <c>%LOCALAPPDATA%\FujiyNotepad\crash.log</c> (beside settings.json).</summary>
        public static CrashLogger Default()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FujiyNotepad");
            return new CrashLogger(Path.Combine(dir, "crash.log"));
        }

        /// <summary>
        /// Appends a timestamped entry for <paramref name="error"/>. Returns <c>true</c> when the entry was
        /// written, <c>false</c> for a null exception or any (swallowed) I/O failure.
        /// </summary>
        public bool Log(Exception? error)
        {
            if (error is null)
            {
                return false;
            }
            return Write(error.GetType().FullName ?? nameof(Exception), error.Message, error.StackTrace);
        }

        /// <summary>
        /// Appends a timestamped entry from already-extracted fields. Used by the handler (the WinUI event
        /// args can surface a null <see cref="Exception"/> but still carry a message) and by tests.
        /// Best-effort: swallows any I/O failure and returns <c>false</c>.
        /// </summary>
        public bool Write(string type, string? message, string? stackTrace)
        {
            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var sb = new StringBuilder();
                sb.Append("===== ")
                  .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                  .AppendLine(" =====");
                sb.Append(string.IsNullOrEmpty(type) ? nameof(Exception) : type);
                if (!string.IsNullOrEmpty(message))
                {
                    sb.Append(": ").Append(message);
                }
                sb.AppendLine();
                if (!string.IsNullOrEmpty(stackTrace))
                {
                    sb.AppendLine(stackTrace);
                }
                sb.AppendLine();

                File.AppendAllText(filePath, sb.ToString());
                return true;
            }
            catch
            {
                // Best-effort: a crash logger must never throw — that would mask the original failure.
                return false;
            }
        }
    }
}
