using System.Globalization;

namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// The viewer's parsed command line (issue #102): the file <see cref="Path"/> to open and an optional
    /// 1-based <see cref="Line"/> / <see cref="Column"/> to jump to once the file is open. A location can be
    /// given as <c>--line N [--column N]</c> options or as a trailing <c>path:line[:col]</c> suffix. The suffix
    /// is only honoured when the path before it exists on disk, so a Windows drive colon (<c>C:\</c>) — or a
    /// real colon in a name — is never mistaken for a line separator. Pure and unit-testable; the host supplies
    /// the file-existence check.
    /// </summary>
    public sealed record CliArguments(string? Path, bool Stdin, int? Line, int? Column)
    {
        /// <summary>
        /// Parses <paramref name="args"/> (the arguments after the executable). The first non-option token is the
        /// path; a lone <c>-</c> means "read standard input"; <paramref name="fileExists"/> disambiguates a
        /// trailing <c>:line[:col]</c> from a drive colon.
        /// </summary>
        public static CliArguments Parse(IReadOnlyList<string> args, Func<string, bool> fileExists)
        {
            string? path = null;
            bool stdin = false;
            int? line = null;
            int? column = null;

            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                if (a == "-")
                {
                    stdin = true;
                }
                else if ((a == "--line" || a == "-l") && i + 1 < args.Count && TryParsePositive(args[i + 1], out int l))
                {
                    line = l;
                    i++;
                }
                else if ((a == "--column" || a == "-c") && i + 1 < args.Count && TryParsePositive(args[i + 1], out int c))
                {
                    column = c;
                    i++;
                }
                else if (path is null && a.Length > 0 && a[0] != '-')
                {
                    path = a; // the first non-option token is the file path
                }
            }

            // A trailing path:line[:col] only when --line wasn't given explicitly.
            if (path is not null && line is null)
            {
                return SplitTrailingLocation(path, stdin, column, fileExists);
            }

            return new CliArguments(path, stdin, line, column);
        }

        // Peels a trailing ":line" or ":line:col" off the path, but only when the remaining path exists on disk.
        private static CliArguments SplitTrailingLocation(string path, bool stdin, int? column, Func<string, bool> exists)
        {
            if (exists(path))
            {
                return new CliArguments(path, stdin, null, column);
            }

            // path:line  (the last ':' must be past a drive colon at index 1, and be a positive integer)
            int c1 = path.LastIndexOf(':');
            if (c1 > 1 && TryParsePositive(path.AsSpan(c1 + 1), out int n1))
            {
                string before = path.Substring(0, c1);
                if (exists(before))
                {
                    return new CliArguments(before, stdin, n1, column);
                }

                // path:line:col
                int c2 = before.LastIndexOf(':');
                if (c2 > 1 && TryParsePositive(before.AsSpan(c2 + 1), out int n2) && exists(before.Substring(0, c2)))
                {
                    return new CliArguments(before.Substring(0, c2), stdin, n2, n1);
                }
            }

            return new CliArguments(path, stdin, null, column);
        }

        private static bool TryParsePositive(ReadOnlySpan<char> s, out int value) =>
            int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
    }
}
