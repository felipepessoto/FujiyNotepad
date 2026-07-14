namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the diagnostic log for swallowed non-fatal exceptions (issue #143): it records context, exception
    /// type and message, and de-duplicates a failure that recurs under the same context so a per-tick fault is
    /// logged once. Device-free over a temp-file-backed <see cref="CrashLogger"/> sink.
    /// </summary>
    public class DiagnosticLogTests
    {
        private static string TempDir() =>
            Path.Combine(Path.GetTempPath(), "fujiy-diag-" + Guid.NewGuid().ToString("N"));

        private static DiagnosticLog At(string path) => new(new CrashLogger(path));

        [Fact]
        public void LogSwallowed_WritesContextTypeAndMessage()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "diagnostics.log");
            try
            {
                bool ok = At(path).LogSwallowed("FileWatcher", new IOException("access denied"));

                Assert.True(ok);
                string text = File.ReadAllText(path);
                Assert.Contains("FileWatcher", text);
                Assert.Contains("System.IO.IOException", text);
                Assert.Contains("access denied", text);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void LogSwallowed_DeDupesConsecutiveIdenticalReportsForSameContext()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "diagnostics.log");
            try
            {
                var log = At(path);

                Assert.True(log.LogSwallowed("TailRefresh", new IOException("boom")));
                Assert.False(log.LogSwallowed("TailRefresh", new IOException("boom")));
                Assert.False(log.LogSwallowed("TailRefresh", new IOException("boom")));

                // Only the first of the identical run was written.
                int count = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), "TailRefresh").Count;
                Assert.Equal(1, count);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void LogSwallowed_DifferentMessageForSameContext_LogsAgain()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "diagnostics.log");
            try
            {
                var log = At(path);

                Assert.True(log.LogSwallowed("TailRefresh", new IOException("first")));
                Assert.True(log.LogSwallowed("TailRefresh", new IOException("second")));

                string text = File.ReadAllText(path);
                Assert.Contains("first", text);
                Assert.Contains("second", text);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void LogSwallowed_DifferentContexts_AreTrackedIndependently()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "diagnostics.log");
            try
            {
                var log = At(path);
                var ex = new IOException("same");

                // The same signature under two different contexts is written for each.
                Assert.True(log.LogSwallowed("FileWatcher", ex));
                Assert.True(log.LogSwallowed("StopIndexing", ex));
                // ...but a repeat of either is de-duped.
                Assert.False(log.LogSwallowed("FileWatcher", ex));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void LogSwallowed_NullException_ReturnsFalseAndWritesNothing()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "diagnostics.log");
            try
            {
                Assert.False(At(path).LogSwallowed("FileWatcher", null));
                Assert.False(File.Exists(path));
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void LogSwallowed_NullOrBlankContext_ReturnsFalseWithoutThrowing(string? context)
        {
            int writes = 0;
            var log = new DiagnosticLog((_, _, _) => { writes++; return true; });

            Assert.False(log.LogSwallowed(context!, new IOException("boom")));
            Assert.Equal(0, writes);
        }

        [Fact]
        public void LogSwallowed_FailedWrite_DoesNotCacheSignature_SoNextAttemptRetries()
        {
            int writes = 0;
            bool writeSucceeds = false;
            var log = new DiagnosticLog((_, _, _) => { writes++; return writeSucceeds; });
            var error = new IOException("boom");

            // Write fails: each identical call must re-attempt (not be de-duped), since nothing was recorded.
            Assert.False(log.LogSwallowed("TailRefresh", error));
            Assert.False(log.LogSwallowed("TailRefresh", error));
            Assert.Equal(2, writes);

            // Once the write succeeds it is recorded and counted...
            writeSucceeds = true;
            Assert.True(log.LogSwallowed("TailRefresh", error));
            Assert.Equal(3, writes);

            // ...and now the identical failure is de-duped (no further write attempt).
            Assert.False(log.LogSwallowed("TailRefresh", error));
            Assert.Equal(3, writes);
        }
    }
}
