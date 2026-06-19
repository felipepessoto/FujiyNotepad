namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the command-line parser (issue #102): the path, the <c>--line</c>/<c>--column</c> options, and the
    /// trailing <c>path:line[:col]</c> suffix — including the Windows drive-colon disambiguation.
    /// </summary>
    public class CliArgumentsTests
    {
        private static Func<string, bool> Existing(params string[] paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            return p => set.Contains(p);
        }

        [Fact]
        public void Parse_PlainPath_NoLocation()
        {
            var r = CliArguments.Parse(new[] { @"C:\logs\app.log" }, Existing(@"C:\logs\app.log"));
            Assert.Equal(@"C:\logs\app.log", r.Path);
            Assert.Null(r.Line);
            Assert.Null(r.Column);
        }

        [Fact]
        public void Parse_LineAndColumnOptions()
        {
            var r = CliArguments.Parse(new[] { @"C:\app.log", "--line", "1234", "--column", "7" }, Existing(@"C:\app.log"));
            Assert.Equal(@"C:\app.log", r.Path);
            Assert.Equal(1234, r.Line);
            Assert.Equal(7, r.Column);
        }

        [Fact]
        public void Parse_ShortOptions()
        {
            var r = CliArguments.Parse(new[] { "file.txt", "-l", "42", "-c", "3" }, Existing("file.txt"));
            Assert.Equal(42, r.Line);
            Assert.Equal(3, r.Column);
        }

        [Fact]
        public void Parse_TrailingLineSuffix()
        {
            var r = CliArguments.Parse(new[] { "app.log:88" }, Existing("app.log"));
            Assert.Equal("app.log", r.Path);
            Assert.Equal(88, r.Line);
        }

        [Fact]
        public void Parse_TrailingLineColumnSuffix()
        {
            var r = CliArguments.Parse(new[] { "app.log:88:5" }, Existing("app.log"));
            Assert.Equal("app.log", r.Path);
            Assert.Equal(88, r.Line);
            Assert.Equal(5, r.Column);
        }

        [Fact]
        public void Parse_TrailingSuffix_OnWindowsPath_DoesNotMistakeDriveColon()
        {
            var r = CliArguments.Parse(new[] { @"C:\logs\app.log:1234" }, Existing(@"C:\logs\app.log"));
            Assert.Equal(@"C:\logs\app.log", r.Path);
            Assert.Equal(1234, r.Line);
        }

        [Fact]
        public void Parse_WindowsPath_NoSuffix_KeepsTheWholePath()
        {
            // The full path exists, so the drive colon is left alone and no line is parsed.
            var r = CliArguments.Parse(new[] { @"C:\logs\app.log" }, Existing(@"C:\logs\app.log"));
            Assert.Equal(@"C:\logs\app.log", r.Path);
            Assert.Null(r.Line);
        }

        [Fact]
        public void Parse_SuffixNotHonoured_WhenStrippedPathDoesNotExist()
        {
            var r = CliArguments.Parse(new[] { "weird:99" }, Existing("something-else"));
            Assert.Equal("weird:99", r.Path);
            Assert.Null(r.Line);
        }

        [Fact]
        public void Parse_ExplicitLineOption_SkipsSuffixParsing()
        {
            var r = CliArguments.Parse(new[] { @"C:\a.log", "--line", "5" }, Existing(@"C:\a.log"));
            Assert.Equal(@"C:\a.log", r.Path);
            Assert.Equal(5, r.Line);
        }

        [Theory]
        [InlineData("-3")]
        [InlineData("0")]
        [InlineData("abc")]
        public void Parse_NonPositiveOrNonNumericLine_Ignored(string value)
        {
            var r = CliArguments.Parse(new[] { "file.txt", "--line", value }, Existing("file.txt"));
            Assert.Equal("file.txt", r.Path);
            Assert.Null(r.Line);
        }

        [Fact]
        public void Parse_NoArgs_NoPath()
        {
            var r = CliArguments.Parse(System.Array.Empty<string>(), Existing());
            Assert.Null(r.Path);
            Assert.Null(r.Line);
        }

        [Fact]
        public void Parse_Dash_MeansStdin()
        {
            var r = CliArguments.Parse(new[] { "-" }, Existing());
            Assert.True(r.Stdin);
            Assert.Null(r.Path);
        }

        [Fact]
        public void Parse_PlainPath_IsNotStdin()
        {
            var r = CliArguments.Parse(new[] { "file.txt" }, Existing("file.txt"));
            Assert.False(r.Stdin);
            Assert.Equal("file.txt", r.Path);
        }
    }
}
