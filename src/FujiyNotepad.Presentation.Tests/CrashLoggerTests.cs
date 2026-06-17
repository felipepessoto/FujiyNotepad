namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the crash logger's file output, append behaviour, directory creation, and best-effort failure
    /// handling. This is the device-free half of the global unhandled-exception handler (issue #77).
    /// </summary>
    public class CrashLoggerTests
    {
        private static string TempDir() =>
            Path.Combine(Path.GetTempPath(), "fujiy-crash-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void Log_WritesTypeMessageAndStackTrace()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "crash.log");
            try
            {
                Exception captured;
                try { throw new InvalidOperationException("boom happened"); }
                catch (InvalidOperationException ex) { captured = ex; }

                bool ok = new CrashLogger(path).Log(captured);

                Assert.True(ok);
                string text = File.ReadAllText(path);
                Assert.Contains("System.InvalidOperationException", text);
                Assert.Contains("boom happened", text);
                // A thrown-and-caught exception has a stack trace, so it should be logged.
                Assert.Contains("CrashLoggerTests", text);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Log_CreatesMissingDirectory()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "nested", "crash.log");
            try
            {
                Assert.True(new CrashLogger(path).Log(new Exception("x")));
                Assert.True(File.Exists(path));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Log_AppendsMultipleEntries()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "crash.log");
            try
            {
                var logger = new CrashLogger(path);
                logger.Log(new InvalidOperationException("first"));
                logger.Log(new ArgumentException("second"));

                string text = File.ReadAllText(path);
                Assert.Contains("first", text);
                Assert.Contains("second", text);
                // Two timestamped headers => two entries appended (not overwritten).
                int headers = text.Split("=====").Length - 1;
                Assert.Equal(4, headers); // each entry has an opening and closing "=====" on its header line
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Log_NullException_ReturnsFalseAndWritesNothing()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "crash.log");
            try
            {
                Assert.False(new CrashLogger(path).Log(null));
                Assert.False(File.Exists(path));
            }
            finally
            {
                if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
            }
        }

        [Fact]
        public void Write_NoStackTrace_StillWritesTypeAndMessage()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "crash.log");
            try
            {
                Assert.True(new CrashLogger(path).Write("UnhandledException", "marshalled message", null));

                string text = File.ReadAllText(path);
                Assert.Contains("UnhandledException", text);
                Assert.Contains("marshalled message", text);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Write_InvalidPath_ReturnsFalseWithoutThrowing()
        {
            // A path whose "directory" is actually an existing file can't be created — the logger must
            // swallow the failure and return false rather than throw from the crash handler.
            string dir = TempDir();
            Directory.CreateDirectory(dir);
            string fileAsDir = Path.Combine(dir, "iamafile");
            File.WriteAllText(fileAsDir, "not a directory");
            string path = Path.Combine(fileAsDir, "crash.log");
            try
            {
                Assert.False(new CrashLogger(path).Write("T", "m", null));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
