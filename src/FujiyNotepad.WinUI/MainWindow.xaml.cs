using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using FujiyNotepad.Core;
using FujiyNotepad.WinUI.Controls;
using FujiyNotepad.Presentation;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics;
using Windows.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace FujiyNotepad.WinUI
{
    public sealed partial class MainWindow : Window
    {
        private IByteSource? source;
        private TextSearcher searcher = null!;
        private LineProvider? provider;
        private LineIndexer LineIndexer = null!;

        // The path of the open file and its active text encoding; encodingAutoDetect tracks whether the
        // encoding was auto-detected (vs. chosen from the Encoding menu) so re-opening can re-detect.
        private string? currentFilePath;
        private TextEncoding currentEncoding = TextEncoding.Utf8;
        private bool encodingAutoDetect = true;

        // Watches the open file's directory so an external change (e.g. a growing log) can be surfaced as a
        // non-blocking "file changed" hint in the status bar. fileChangeSignaled coalesces the burst of events
        // a single save produces so only the first marshals to the UI thread.
        private FileSystemWatcher? fileWatcher;
        private int fileChangeSignaled;

        // When a Reload preserves the scroll position, the target first-visible line is re-applied on each index
        // tick (until reachable) because a just-reloaded large file is only indexed up to its frontier. -1 = none.
        private int pendingRestoreFirstLine = -1;
        private int pendingGoToLine = -1;
        private int pendingGoToColumn;
        private double pendingRestoreHorizontalOffset;

        // Session restore (issue #51): on launch, reopen the last file at its saved scroll + caret position. Like
        // the other pending-restore mechanisms, the target is re-applied on each index tick until the index
        // reaches it (a large file only indexes up to its frontier at open). -1 = nothing pending.
        private int pendingSessionFirstLine = -1;
        private int pendingSessionCaretLine;
        private int pendingSessionCaretColumn;

        private readonly DispatcherQueueTimer indexRefreshTimer;
        private readonly DispatcherQueueTimer findPreviewTimer;
        private readonly DispatcherQueueTimer followTailTimer;
        private CancellationTokenSource? cancelIndexing;

        // Tail / follow mode (issue #28): poll the file size, resume indexing over appended bytes, and (while the
        // user is at the bottom) stick to the new end. `tail` tracks the last observed length.
        private bool followTail;
        private bool tailSnapToBottom;
        private TailController? tail;

        // Stdin piping (issue #103): when launched with `-` (or a redirected stdin and no path), incoming bytes
        // are spooled to a temp file that is opened and followed like a live log. These track that temp file so
        // the spool can be cancelled and the file deleted on close or when another file is opened.
        private string? stdinTempPath;
        private CancellationTokenSource? stdinCts;
        private Task indexingTask = Task.CompletedTask;
        private bool syncingScroll;

        private readonly SettingsStore settingsStore = SettingsStore.Default();
        private AppSettings settings = new();
        private SizeInt32 lastNormalSize;

        // Up/Down search-history recall state for the Find and Filter boxes (SearchHistoryNavigator is pure and
        // unit-tested). Each is rebuilt lazily and reset when its bar opens or a search is recorded.
        private SearchHistoryNavigator? findNav;
        private SearchHistoryNavigator? filterNav;

        // Find state: the coordinator decides where each "find next/previous" starts and how the wrap/caret
        // state evolves (FindCoordinator, headlessly tested); findCts cancels an in-progress search; isFinding
        // guards against starting a second search while one runs.
        private readonly FindCoordinator findCoordinator = new();
        private CancellationTokenSource? findCts;
        private bool isFinding;

        // Background match-count state: countedKey caches the last term/options counted; countCts cancels an
        // in-flight count; suppressFindOptionEvents silences the toggle handlers while applying saved options.
        private CancellationTokenSource? countCts;
        private string? countedKey;
        private bool suppressFindOptionEvents;

        // Bucketed positions of the current find's matches, painted on the scrollbar marker margin (null when
        // there is no active find). Bounded to MatchMarks.Resolution buckets, so it stays small on huge files.
        private MatchMarks? matchMarks;

        // Cancels an in-flight background character count (re-run on file open / encoding change).
        private CancellationTokenSource? charCountCts;

        // Above this size, skip the automatic full-file character count (a multi-GB decode pass on every open)
        // and instead show the byte size with an on-demand "Count characters" action (issue #39). Single-byte
        // encodings are exempt: their character count equals the byte count, so it is shown without a decode.
        private const long AutoCharCountByteLimit = 256L * 1024 * 1024;

        // Filter / grep view: while active (filteredSource is not null), the engine renders only the matching
        // lines (a FilteredLineSource wrapping the real provider) and filterCts cancels an in-flight scan.
        private FilteredLineSource? filteredSource;
        private CancellationTokenSource? filterCts;

        public MainWindow()
        {
            this.InitializeComponent();

            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "FujiyNotepad.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }

            settings = settingsStore.Load();
            ApplyStartupSettings();

            View.ViewChanged += SyncScrollBars;
            View.CaretChanged += UpdateCursorStatus;
            View.FontChanged += OnFontChanged;

            indexRefreshTimer = DispatcherQueue.CreateTimer();
            indexRefreshTimer.Interval = TimeSpan.FromMilliseconds(150);
            indexRefreshTimer.Tick += IndexRefreshTimer_Tick;

            // Incremental find: a short debounce so the live highlight/count runs after a pause in typing, not
            // on every keystroke. Single-shot, restarted on each change to the term.
            findPreviewTimer = DispatcherQueue.CreateTimer();
            findPreviewTimer.Interval = TimeSpan.FromMilliseconds(200);
            findPreviewTimer.IsRepeating = false;
            findPreviewTimer.Tick += (_, _) => PreviewFind();

            // Tail/follow poll: when "Follow Tail" is on, check the file size ~1/s and pull in appended lines.
            followTailTimer = DispatcherQueue.CreateTimer();
            followTailTimer.Interval = TimeSpan.FromMilliseconds(1000);
            followTailTimer.Tick += FollowTailTimer_Tick;

            Closed += (_, _) =>
            {
                SaveWindowState();
                StopWatchingFile();
                followTailTimer.Stop();
                cancelIndexing?.Cancel();
                findCts?.Cancel();
                source?.Dispose();
                CleanupStdin();
            };

            // Open a file passed on the command line (file association / "open with" / drag-onto-exe), with an
            // optional --line / --column or trailing :line[:col] location to jump to (issue #102). A lone `-`
            // (or a redirected stdin with no path) instead reads piped standard input (issue #103).
            CliArguments cli = CliArguments.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray(), File.Exists);
            if (cli.Path is { } cliPath && File.Exists(cliPath))
            {
                int? cliLine = cli.Line, cliColumn = cli.Column;
                DispatcherQueue.TryEnqueue(() => { _ = OpenFileAt(cliPath, cliLine, cliColumn); });
            }
            else
            {
                // A real redirected stdin (pipe/file) means "read piped input" (#103). Console.IsInputRedirected
                // alone is unreliable for a windows-subsystem process — a null stdin handle reports redirected —
                // so require a usable stream; otherwise fall through to reopening the last file (#51).
                Stream? stdinStream = cli.Path is null ? TryOpenRedirectedStdin() : null;
                if (cli.Stdin || stdinStream is not null)
                {
                    DispatcherQueue.TryEnqueue(() => { _ = OpenFromStdinAsync(stdinStream); });
                }
                else if (settings.RestoreLastSession
                         && settings.LastSessionFilePath is { Length: > 0 } lastPath
                         && File.Exists(lastPath))
                {
                    int first = Math.Max(0, settings.LastSessionFirstVisibleLine);
                    int caretLine = Math.Max(0, settings.LastSessionCaretLine);
                    int caretColumn = Math.Max(0, settings.LastSessionCaretColumn);
                    DispatcherQueue.TryEnqueue(() => { _ = RestoreSessionAsync(lastPath, first, caretLine, caretColumn); });
                }
            }
        }

        // Accept a text file dragged onto the window: show an "Open" affordance while a file is over
        // the window, then open the first dropped file.
        private void Root_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                if (e.DragUIOverride is not null)
                {
                    e.DragUIOverride.Caption = "Open";
                    e.DragUIOverride.IsContentVisible = true;
                }
            }
        }

        private async void Root_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            if (items.OfType<StorageFile>().FirstOrDefault() is { } file)
            {
                await OpenFile(file.Path);
            }
        }

        private async void Open_Click(object sender, RoutedEventArgs e)
        {
            // Modern Windows App SDK desktop picker (constructed from the window id; Native-AOT safe). The
            // legacy Windows.Storage.Pickers.FileOpenPicker required the InitializeWithWindow interop hack.
            var picker = new FileOpenPicker(AppWindow.Id);
            PickFileResult? result = await picker.PickSingleFileAsync();
            if (result is not null && !string.IsNullOrEmpty(result.Path))
            {
                await OpenFile(result.Path);
            }
        }

        private async void OpenSample_Click(object sender, RoutedEventArgs e)
        {
            // Versioned name so an existing cached copy from a previous build is not reused.
            string path = Path.Combine(Path.GetTempPath(), "FujiyNotepadSample-v3.txt");
            if (!File.Exists(path))
            {
                LblStatus.Text = "Generating sample...";
                await Task.Run(() => CreateSampleFile(path));
            }
            await OpenFile(path, addToRecent: false);
        }

        private static void CreateSampleFile(string path)
        {
            using var writer = new StreamWriter(path);

            // A short, self-describing header exercises the text-view features; the large body below also
            // demonstrates big-file handling.
            foreach (string line in SampleHeaderLines())
            {
                writer.WriteLine(line);
            }

            var random = new Random(1);
            for (int i = 1; i <= 10_000_000; i++)
            {
                writer.Write(i);
                writer.Write(" - ");
                writer.WriteLine(new string('x', random.Next(300, 500)));
            }
        }

        private static IEnumerable<string> SampleHeaderLines()
        {
            const string tab = "\t";
            return new[]
            {
                "Fujiy Notepad sample - try the features below, then scroll for 10,000,000 more lines.",
                "===================================================================================",
                "",
                "[Wide / CJK glyphs] Each CJK or fullwidth glyph spans two columns, so these 8-cell",
                "rows share the same '|' marker column as the ASCII row:",
                "12345678|",
                "\u4E2D\u6587\u5B57\u7B26|", // 中文字符 (4 CJK ideographs)
                "\u65E5\u672C\u8A9E\u5B66|", // 日本語学 (kanji)
                "\uD55C\uAD6D\uC5B4\uAE00|", // 한국어글 (Hangul)
                "\uFF26\uFF35\uFF2C\uFF2C|", // ＦＵＬＬ (fullwidth Latin)
                "Mixed a\u4E2Db\u65E5c\uD55Cd - each wide glyph counts as two cells.",
                "",
                "[Tab Width] Switch Edit \u25B8 Tab Width (2 / 4 / 8) and watch these columns realign:",
                "name" + tab + "type" + tab + "notes",
                "id" + tab + "int" + tab + "primary key",
                "x" + tab + "y" + tab + "z",
                "",
                "[Double-click word-select] Double-click a token to select the whole word or run:",
                "double_click selects snake_case_words and CamelCaseWords as single words",
                "punctuation, like; commas. and-hyphens form their own runs",
                "spaces      between      words (double-click a gap to select the run of spaces)",
                "",
                "[Find: Match case] Open Find (Ctrl+F), toggle 'Aa'. Case-insensitive (default) finds all three;",
                "turn Match case on to step through them one at a time:",
                "Widget  widget  WIDGET  -  the same word in three different cases.",
                "",
                "[Find: Whole word] Toggle '[ab]' and find 'cat'. Whole-word matches a token only when it stands",
                "alone, so on the next line the first and last words match - not the 'cat' in category/scatter/bobcat:",
                "cat category scatter bobcat cat",
                "",
                "[Find: Regex] Toggle '.*' and try these patterns (each is matched within a single line):",
                "    \\d{4}-\\d{2}-\\d{2}     dates:   2024-01-15   2025-12-31   1999-07-04",
                "    #[0-9A-Fa-f]{6}        colors:  #1E90FF   #FF0000   #00c853",
                "    \\b\\w+@\\w+\\.\\w+\\b   emails:  alice@example.com   bob@test.org",
                "    TODO|FIXME|HACK        tags:    TODO refactor   FIXME edge case   HACK workaround",
                "",
                "[Find: Match count] The count beside the find bar shows the file-wide total (counted in the",
                "background on this large sample). The repeated word on the next line occurs 7 times, 6 with Match case on:",
                "needle, needle, NEEDLE, needle, needle, needle, needle",
                "",
                "[Log triage] The log block below powers three features at once:",
                "  - Edit \u25B8 Filter... (Ctrl+Shift+F): type ERROR to collapse the file to just the matching",
                "    lines (a live grep); Copy or Save those lines from the filter bar.",
                "  - View \u25B8 Highlight Rules... then Apply: the defaults colour ERROR red and WARN orange.",
                "  - Select the first log line through the last - the status bar shows 'delta = 2m 30s', the",
                "    elapsed time between their timestamps (great for 'how long did this take?').",
                "2024-01-15 09:30:00.000  INFO   service starting",
                "2024-01-15 09:30:00.250  INFO   loading configuration files",
                "2024-01-15 09:30:12.500  WARN   slow disk response (820 ms)",
                "2024-01-15 09:31:05.100  ERROR  database connection timed out",
                "2024-01-15 09:31:05.140  INFO   retrying with backup host",
                "2024-01-15 09:32:30.000  INFO   request completed - 312 rows",
                "",
                "[Show Whitespace] Toggle View \u25B8 Show Whitespace to reveal space dots, tab arrows, trailing-",
                "space emphasis and control-character boxes. The next line packs several together:",
                "a" + tab + "tab, double  spaces, a BEL control char (\u0007), and trailing blanks ->    ",
                "",
                "[Bookmarks] Put the caret on this line and press Ctrl+F2 to bookmark it - a tick appears in the",
                "left margin and on the scrollbar. Scroll into the millions of lines below, then press F2 / Shift+F2",
                "to jump to the next / previous bookmark (Edit \u25B8 Clear All Bookmarks clears them).",
                "",
                "[Selection stats] Select part of a line and the status bar shows the character count; drag across",
                "several lines for a line count too. Edit \u25B8 Copy with Line Numbers (Ctrl+Shift+C) copies the",
                "selection with each line prefixed by its number.",
                "",
                "-----------------------------------------------------------------------------------",
                "Below: 10,000,000 generated lines (large-file demo).",
            };
        }

        private async Task OpenFile(string path, bool addToRecent = true, TextEncoding? forcedEncoding = null, bool preserveView = false)
        {
            // Open the new file before tearing down the current one, so a failure (missing/locked/denied
            // file) leaves the already-open file intact instead of half-closing it.
            FileByteSource newSource;
            try
            {
                newSource = new FileByteSource(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                await ShowOpenErrorAsync(path, ex);
                return;
            }

            // For a Reload, remember where the view was so it can be restored over the rebuilt index.
            int restoreFirstLine = preserveView && provider is not null ? View.FirstVisibleLine : -1;
            double restoreHorizontal = preserveView && provider is not null ? View.HorizontalOffset : 0;

            await StopIndexingAsync();

            // If a previous stdin pipe was open, stop spooling and delete its temp file — unless we are (re)opening
            // that very temp file, which happens when stdin first opens or is reloaded by Follow Tail.
            if (stdinTempPath is not null && !string.Equals(path, stdinTempPath, StringComparison.OrdinalIgnoreCase))
            {
                CleanupStdin();
            }

            source?.Dispose();
            source = newSource;

            // A fresh open starts with follow off; baseline the tail length for when it is enabled.
            followTail = false;
            tailSnapToBottom = false;
            followTailTimer.Stop();
            FollowTailToggle.IsChecked = false;
            tail = new TailController(newSource.Length);

            // Auto-detect the encoding (BOM + heuristic) unless the user forced one from the Encoding menu.
            currentEncoding = forcedEncoding ?? EncodingDetector.Detect(source);
            encodingAutoDetect = forcedEncoding is null;
            currentFilePath = path;
            searcher = new TextSearcher(source);
            LineIndexer = new LineIndexer(searcher, currentEncoding);
            provider = new LineProvider(source, LineIndexer, currentEncoding);
            findCoordinator.Reset();
            countCts?.Cancel();
            countedKey = null;
            FindCount.Text = string.Empty;
            View.SetHighlighter(null);
            matchMarks = null; // the margin is repainted by RefreshMarkerMargin() after SetProvider below
            ResetFilter();

            View.SetProvider(provider);
            RefreshMarkerMargin(); // clear any previous file's bookmark ticks
            // Restore the scroll position (Reload) or start at the top (a new file); the index tick keeps
            // nudging toward the saved line until the rebuilt index reaches it.
            pendingRestoreFirstLine = restoreFirstLine;
            pendingRestoreHorizontalOffset = restoreHorizontal;
            pendingGoToLine = -1; // a plain open cancels any pending command-line jump
            pendingSessionFirstLine = -1; // ...and any pending session restore
            if (restoreFirstLine >= 0)
            {
                View.FirstVisibleLine = restoreFirstLine;
                View.HorizontalOffset = restoreHorizontal;
            }
            Title = LocalizedStrings.Format("WindowTitleWithFile", Path.GetFileName(path));
            EditMenu.IsEnabled = true;
            EncodingMenu.IsEnabled = true;
            ReloadItem.IsEnabled = true;
            CloseItem.IsEnabled = true;
            CopyPathItem.IsEnabled = true;
            RevealItem.IsEnabled = true;
            UpdateEncodingUi();
            _ = RefreshCharacterCountAsync();
            UpdateLineEndingLabel();
            StartWatchingFile(path);

            if (addToRecent)
            {
                settings.RecentFiles = RecentFiles.Add(settings.RecentFiles, path);
                settingsStore.Save(settings);
                RebuildRecentMenu();
            }

            indexRefreshTimer.Start();
            StartIndexing();
            SyncScrollBars();
            View.FocusCanvas();
        }

        // Reloads the currently-open file (F5 / File > Reload / the "file changed" status-bar hint): re-detects
        // the encoding (unless one was chosen from the Encoding menu), rebuilds the index, and refreshes the
        // view while trying to keep the scroll position. It is OpenFile with the current path and preserveView.
        private async void Reload_Click(object sender, RoutedEventArgs e)
        {
            if (currentFilePath is not { } path)
            {
                return;
            }

            TextEncoding? forced = encodingAutoDetect ? null : currentEncoding;
            await OpenFile(path, addToRecent: false, forcedEncoding: forced, preserveView: true);
        }

        // Closes the current file and returns to the empty viewer (issue #51). An explicit close (Ctrl+W /
        // File > Close) is a deliberate "I'm done with this file" signal, so unlike closing the window — which
        // saves the session to resume next launch — it forgets the saved session, matching the well-known
        // browser / editor pattern (a tab you explicitly close is not reopened).
        private async void CloseFile_Click(object sender, RoutedEventArgs e) => await CloseCurrentFileAsync();

        private async Task CloseCurrentFileAsync()
        {
            if (currentFilePath is null && stdinTempPath is null)
            {
                return; // nothing open
            }

            await StopIndexingAsync();
            indexRefreshTimer.Stop();

            // Stop following / watching / spooling and leave any filter view.
            followTail = false;
            tailSnapToBottom = false;
            followTailTimer.Stop();
            FollowTailToggle.IsChecked = false;
            StopWatchingFile();
            CleanupStdin();
            ResetFilter();

            // Drop find / count / highlight state tied to the file.
            findCoordinator.Reset();
            countCts?.Cancel();
            countedKey = null;
            charCountCts?.Cancel();
            FindCount.Text = string.Empty;
            View.SetHighlighter(null);
            matchMarks = null;

            // Tear down the engine + byte source and clear pending navigation.
            View.SetProvider(null);
            RefreshMarkerMargin();
            source?.Dispose();
            source = null;
            provider = null;
            currentFilePath = null;
            encodingAutoDetect = true;
            pendingRestoreFirstLine = -1;
            pendingGoToLine = -1;
            pendingSessionFirstLine = -1;

            // Back to the empty-state UI (mirrors the XAML defaults before any file is opened).
            Title = LocalizedStrings.Get("AppDisplayName");
            EditMenu.IsEnabled = false;
            EncodingMenu.IsEnabled = false;
            ReloadItem.IsEnabled = false;
            CloseItem.IsEnabled = false;
            CopyPathItem.IsEnabled = false;
            RevealItem.IsEnabled = false;
            CountCharsLink.Visibility = Visibility.Collapsed;
            ReloadHint.Visibility = Visibility.Collapsed;
            LblStatus.Text = string.Empty;
            LblCharCount.Text = string.Empty;
            LblEncoding.Text = string.Empty;
            LblLineEnding.Text = string.Empty;
            LblCursor.Text = string.Empty;

            // Explicit close forgets the saved session so this file isn't reopened next launch (the
            // RestoreLastSession preference itself stays on).
            ClearSavedSession();
            settingsStore.Save(settings);

            View.FocusCanvas();
        }

        // (Re)starts watching the open file's folder for external changes and clears any pending hint. Watching
        // the folder (filtered to the file name) survives editors that replace the file via rename. Failures to
        // watch (e.g. an unwatchable path) are non-fatal — Reload still works, there is just no change hint.
        private void StartWatchingFile(string path)
        {
            StopWatchingFile();
            Interlocked.Exchange(ref fileChangeSignaled, 0);
            ReloadHint.Visibility = Visibility.Collapsed;

            string? dir = Path.GetDirectoryName(path);
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
            {
                return;
            }

            try
            {
                var watcher = new FileSystemWatcher(dir, name)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                };
                watcher.Changed += OnWatchedFileChanged;
                watcher.Created += OnWatchedFileChanged;
                watcher.Deleted += OnWatchedFileChanged;
                watcher.Renamed += OnWatchedFileChanged;
                watcher.EnableRaisingEvents = true;
                fileWatcher = watcher;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or System.Security.SecurityException)
            {
                fileWatcher = null;
            }
        }

        private void StopWatchingFile()
        {
            if (fileWatcher is { } watcher)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnWatchedFileChanged;
                watcher.Created -= OnWatchedFileChanged;
                watcher.Deleted -= OnWatchedFileChanged;
                watcher.Renamed -= OnWatchedFileChanged;
                watcher.Dispose();
                fileWatcher = null;
            }
        }

        // Fires on a thread-pool thread for every change event; coalesce the burst into a single UI update and
        // reveal the non-blocking "file changed" hint. The user decides whether to reload — nothing is forced.
        private void OnWatchedFileChanged(object sender, FileSystemEventArgs e)
        {
            // While following the tail (issue #28) the appended bytes — including stdin spooling, #103 — are
            // already pulled in live, so the "file changed, reload" hint would be misleading noise.
            if (followTail)
            {
                return;
            }

            if (Interlocked.Exchange(ref fileChangeSignaled, 1) == 0)
            {
                DispatcherQueue.TryEnqueue(() => ReloadHint.Visibility = Visibility.Visible);
            }
        }

        // ----- Tail / follow mode (issue #28): live auto-reload of a growing file -----

        private void FollowTail_Click(object sender, RoutedEventArgs e)
        {
            if (FollowTailToggle.IsChecked)
            {
                EnableFollowTail();
            }
            else
            {
                DisableFollowTail();
            }
        }

        // Starts following: jump to the end now, baseline the size, and poll for growth. The follow is sticky —
        // it keeps the view pinned to the end only while the user stays at the bottom. With <paramref name="rebaseline"/>
        // false (stdin, #103) the open-time baseline is kept so the poll still indexes bytes spooled in after the
        // initial open — otherwise a fast producer that finishes between open and here would be missed.
        private void EnableFollowTail(bool rebaseline = true)
        {
            if (provider is null || source is null)
            {
                FollowTailToggle.IsChecked = false;
                return;
            }

            followTail = true;
            FollowTailToggle.IsChecked = true;
            if (rebaseline || tail is null)
            {
                tail = new TailController(source.RefreshLength());
            }
            tailSnapToBottom = true;
            View.FirstVisibleLine = View.MaxFirstLine; // jump to the end immediately
            View.FocusCanvas();
            SetLineCountStatus();
            followTailTimer.Start();
        }

        private void DisableFollowTail()
        {
            followTail = false;
            tailSnapToBottom = false;
            followTailTimer.Stop();
            FollowTailToggle.IsChecked = false;
            SetLineCountStatus();
        }

        // Poll tick: pull in appended lines and (when sticky) follow the end; a shrink means truncation/rotation.
        private async void FollowTailTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (!followTail || provider is null || source is null || currentFilePath is null)
            {
                followTailTimer.Stop();
                return;
            }
            // Don't fight the filter view or an in-progress (initial or prior-grow) index; retry next tick.
            if (filteredSource is not null || !LineIndexer.IsCompleted || tail is null)
            {
                return;
            }

            long current;
            try
            {
                current = source.RefreshLength();
            }
            catch
            {
                return; // a transient read failure mid-rotation; retry next tick
            }

            switch (tail.Observe(current))
            {
                case TailChange.Grew:
                    // Snap to the new end afterwards only if the user is currently at the bottom.
                    tailSnapToBottom = TailController.ShouldStickToBottom(View.FirstVisibleLine, View.MaxFirstLine);
                    LineIndexer.IsCompleted = false; // hide the open line until its newline is found
                    provider.RefreshLength();         // new size + endsWithNewline, drop the stale last line
                    indexRefreshTimer.Start();        // push the growing line count and (sticky) follow the end
                    StartIndexing();                  // resume the index over the appended region
                    break;

                case TailChange.Shrunk:
                    await ReloadForTailAsync();        // truncation / rotation: reset and re-index
                    break;
            }
        }

        // Reloads the file after a truncation/rotation, re-enabling follow so the view keeps tailing the end.
        private async Task ReloadForTailAsync()
        {
            string path = currentFilePath!;
            bool wasFollowing = followTail;
            followTailTimer.Stop();
            await OpenFile(path, addToRecent: false); // resets the index and follow state
            if (wasFollowing && currentFilePath == path)
            {
                EnableFollowTail();
            }
        }

        // Status-bar line count, with a "Following" suffix while tail mode is on.
        private void SetLineCountStatus()
        {
            if (provider is null || filteredSource is not null)
            {
                return;
            }
            string text = StatusText.LineCount(provider.LineCount);
            LblStatus.Text = followTail ? $"{text}  \u00B7  {LocalizedStrings.Get("StatusFollowingSuffix")}" : text;
        }

        private async Task ShowOpenErrorAsync(string path, Exception ex)
        {
            string reason = ex switch
            {
                FileNotFoundException => LocalizedStrings.Get("ErrorFileNotFound"),
                DirectoryNotFoundException => LocalizedStrings.Get("ErrorDirNotFound"),
                UnauthorizedAccessException => LocalizedStrings.Get("ErrorNoPermission"),
                _ => ex.Message,
            };

            var dialog = new ContentDialog
            {
                Title = LocalizedStrings.Get("OpenErrorTitle"),
                Content = LocalizedStrings.Format("OpenErrorContent", Path.GetFileName(path), reason),
                CloseButtonText = LocalizedStrings.Get("DialogOk"),
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        // Re-opens the current file with the encoding chosen from the menu (empty tag = auto-detect).
        private async void Encoding_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioMenuFlyoutItem item && currentFilePath is { } path)
            {
                TextEncoding? forced = item.Tag is string id && id.Length > 0 ? TextEncoding.FromId(id) : null;
                await OpenFile(path, addToRecent: false, forcedEncoding: forced);
            }
        }

        // Reflects the active encoding in the status bar and ticks the matching menu item (or "Auto-detect").
        private void UpdateEncodingUi()
        {
            LblEncoding.Text = encodingAutoDetect ? $"{currentEncoding.DisplayName} (auto)" : currentEncoding.DisplayName;

            EncAuto.IsChecked = encodingAutoDetect;
            EncUtf8.IsChecked = !encodingAutoDetect && currentEncoding.Id == "utf-8";
            EncUtf8Bom.IsChecked = !encodingAutoDetect && currentEncoding.Id == "utf-8-bom";
            EncUtf16Le.IsChecked = !encodingAutoDetect && currentEncoding.Id == "utf-16le";
            EncUtf16Be.IsChecked = !encodingAutoDetect && currentEncoding.Id == "utf-16be";
            EncUtf32Le.IsChecked = !encodingAutoDetect && currentEncoding.Id == "utf-32le";
            EncUtf32Be.IsChecked = !encodingAutoDetect && currentEncoding.Id == "utf-32be";
            EncWindows1252.IsChecked = !encodingAutoDetect && currentEncoding.Id == "windows-1252";
        }

        // Shows the file's newline convention (LF / CRLF / Mixed) in the status bar, detected from a leading
        // sample in the current encoding. Blank when no newline is found or no file is open.
        private void UpdateLineEndingLabel()
        {
            LblLineEnding.Text = source is null
                ? string.Empty
                : LineEndingDetector.ToLabel(LineEndingDetector.Detect(source, currentEncoding));
        }

        private void StartIndexing()
        {
            StartIndexingItem.IsEnabled = false;
            StopIndexingItem.IsEnabled = true;
            cancelIndexing = new CancellationTokenSource();
            CancellationToken token = cancelIndexing.Token;
            var progress = new Progress<int>(p => LblStatus.Text = StatusText.IndexProgress(p));
            indexingTask = Task.Run(async () =>
            {
                try
                {
                    await LineIndexer.StartTaskToIndexLines(token, progress);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled because another file is being opened or the window is closing.
                }
            }, token);
        }

        private async Task StopIndexingAsync()
        {
            cancelIndexing?.Cancel();
            try
            {
                await indexingTask;
            }
            catch
            {
                // Tearing down the previous file's indexing; any failure is irrelevant to the switch.
            }
        }

        private void IndexRefreshTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (provider is null)
            {
                indexRefreshTimer.Stop();
                return;
            }

            // While the filter view is active the engine renders the fixed matching-line set, so don't push the
            // real provider's (growing) line count into it; just keep the timer alive until indexing finishes.
            if (filteredSource is null)
            {
                View.UpdateTotalLines(provider.LineCount);
                RefreshMarkerMargin(); // bookmark tick positions shift as the total line count grows

                // Sticky-bottom follow: keep the view pinned to the new end as appended lines arrive.
                if (followTail && tailSnapToBottom)
                {
                    View.FirstVisibleLine = View.MaxFirstLine;
                }

                // Keep nudging a reloaded view toward its saved scroll position as the rebuilt index grows; stop
                // once the target line is reachable (or indexing finished) so it no longer fights the user.
                if (pendingRestoreFirstLine >= 0)
                {
                    View.FirstVisibleLine = pendingRestoreFirstLine;
                    View.HorizontalOffset = pendingRestoreHorizontalOffset;
                    if (View.MaxFirstLine >= pendingRestoreFirstLine || LineIndexer.IsCompleted)
                    {
                        pendingRestoreFirstLine = -1;
                    }
                }

                ApplyPendingGoTo();
                ApplyPendingSession();
            }

            if (LineIndexer.IsCompleted)
            {
                indexRefreshTimer.Stop();
                StopIndexingItem.IsEnabled = false;
                if (filteredSource is null)
                {
                    SetLineCountStatus();
                }
            }
        }

        private void StartIndexing_Click(object sender, RoutedEventArgs e)
        {
            // Resume indexing from where it was stopped (the index is append-only and continues).
            if (provider is null || LineIndexer.IsCompleted)
            {
                return;
            }
            indexRefreshTimer.Start();
            StartIndexing();
        }

        private void StopIndexing_Click(object sender, RoutedEventArgs e)
        {
            cancelIndexing?.Cancel();
            indexRefreshTimer.Stop();
            StartIndexingItem.IsEnabled = true;
            StopIndexingItem.IsEnabled = false;
            if (provider is not null)
            {
                LblStatus.Text = StatusText.LineCount(provider.LineCount) + " (indexing stopped)";
            }
        }

        private void SyncScrollBars()
        {
            if (syncingScroll)
            {
                return;
            }

            syncingScroll = true;
            try
            {
                int viewportLines = View.FullyVisibleLineCount;
                VScroll.Maximum = View.MaxFirstLine;
                VScroll.ViewportSize = viewportLines;
                VScroll.LargeChange = viewportLines;
                VScroll.SmallChange = 1;
                VScroll.Value = View.FirstVisibleLine;

                double viewportWidth = View.ViewportWidthPx;
                HScroll.Maximum = Math.Max(0, View.HorizontalExtentPx - viewportWidth);
                HScroll.ViewportSize = viewportWidth;
                HScroll.SmallChange = View.CharWidthPx > 0 ? View.CharWidthPx : 8;
                HScroll.LargeChange = viewportWidth;
                HScroll.Value = View.HorizontalOffset;
            }
            finally
            {
                syncingScroll = false;
            }
        }

        // The horizontal scrollbar is meaningless while word-wrap is on (there is no horizontal overflow), so
        // hide it; show it again when wrap is off.
        private void UpdateScrollBarVisibility()
        {
            HScroll.Visibility = View.WordWrap ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateCursorStatus(TextPosition pos)
        {
            // While filtering, the engine's line index is a filtered row; show the real source line number.
            int displayLine = filteredSource is not null && pos.Line >= 0 && pos.Line < filteredSource.LineCount
                ? filteredSource.SourceLineAt(pos.Line)
                : pos.Line;

            LblCursor.Text = StatusText.CursorStatus(
                displayLine, pos.Column, View.GetSelectionStats(), View.GetSelectionTimestampDelta());

            UpdateSelectionHighlight();
        }

        // Highlights all occurrences of the selected text (issue #130). Driven by every caret/selection change
        // (this is called from UpdateCursorStatus). The engine stands the highlight down while a Find highlight
        // is active, so Find always wins; opening/closing a file or entering/leaving the filter collapses the
        // selection and raises CaretChanged, which clears the highlighter here. The last applied term is cached
        // so the common case (caret navigation with no selection) is a no-op — no highlighter rebuild, no redraw.
        private string? selectionHighlightTerm;

        private void UpdateSelectionHighlight()
        {
            string? term = HighlightSelectionToggle.IsChecked
                ? SelectionHighlightPolicy.TermFor(View.GetSelectedTextOnSingleLine(), isSingleLine: true)
                : null;

            if (term == selectionHighlightTerm)
            {
                return;
            }

            selectionHighlightTerm = term;
            View.SetSelectionHighlighter(
                term is null ? null : new LiteralLineHighlighter(term, ignoreCase: false, wholeWord: false));
        }

        private void VScroll_Scroll(object sender, ScrollEventArgs e)
        {
            if (syncingScroll)
            {
                return;
            }
            View.FirstVisibleLine = (int)Math.Round(e.NewValue);
        }

        private void HScroll_Scroll(object sender, ScrollEventArgs e)
        {
            if (syncingScroll)
            {
                return;
            }
            View.HorizontalOffset = e.NewValue;
        }

        private async void GoTo_Click(object sender, RoutedEventArgs e)
        {
            if (provider is null)
            {
                return;
            }

            ExitFilterToFullView();
            var input = new TextBox { PlaceholderText = LocalizedStrings.Get("GoToLinePlaceholder") };
            var dialog = new ContentDialog
            {
                Title = LocalizedStrings.Get("GoToLineTitle"),
                Content = input,
                PrimaryButtonText = LocalizedStrings.Get("DialogGo"),
                CloseButtonText = LocalizedStrings.Get("DialogCancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };

            // Pressing Enter in the box confirms the dialog, the same as clicking "Go".
            bool confirmedByEnter = false;
            input.KeyDown += (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    args.Handled = true;
                    confirmedByEnter = true;
                    dialog.Hide();
                }
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if ((confirmedByEnter || result == ContentDialogResult.Primary) && int.TryParse(input.Text, out int line))
            {
                View.GoToLine(line - 1);
                View.FocusCanvas();
            }
        }

        // Opens a file and, when a 1-based line/column was given on the command line, jumps there once indexing
        // reaches it. GoToLineColumn clamps to the indexed frontier, so the IndexRefreshTimer nudges toward the
        // target as the index grows, exactly like a reloaded scroll position is restored (issue #102).
        private async Task OpenFileAt(string path, int? line, int? column)
        {
            await OpenFile(path);
            if (line is { } ln)
            {
                pendingGoToLine = Math.Max(0, ln - 1);
                pendingGoToColumn = column is { } col ? Math.Max(0, col - 1) : 0;
                ApplyPendingGoTo();
            }
        }

        private void ApplyPendingGoTo()
        {
            if (pendingGoToLine < 0 || provider is null || filteredSource is not null)
            {
                return;
            }

            View.GoToLineColumn(pendingGoToLine, pendingGoToColumn);
            if (provider.LineCount > pendingGoToLine || LineIndexer.IsCompleted)
            {
                pendingGoToLine = -1; // the target line is now indexed; stop nudging
            }
        }

        // ----- Session restore (issue #51): reopen the last file where it was left -----

        // Reopens the last file (called at launch when no file/pipe was given and the setting is on) and queues
        // its saved scroll + caret position for restore as the index catches up.
        private async Task RestoreSessionAsync(string path, int firstVisibleLine, int caretLine, int caretColumn)
        {
            await OpenFile(path); // OpenFile resets the pending-session state, so set it afterwards
            pendingSessionFirstLine = firstVisibleLine;
            pendingSessionCaretLine = caretLine;
            pendingSessionCaretColumn = caretColumn;
            ApplyPendingSession();
        }

        private void ApplyPendingSession()
        {
            if (pendingSessionFirstLine < 0 || provider is null || filteredSource is not null)
            {
                return;
            }

            // Restore the caret first (this scrolls its line to the top), then force the saved scroll position so
            // the view shows exactly what the user last saw -- which need not be the caret's line.
            View.GoToLineColumn(pendingSessionCaretLine, pendingSessionCaretColumn);
            View.FirstVisibleLine = pendingSessionFirstLine;

            if ((provider.LineCount > pendingSessionCaretLine && View.MaxFirstLine >= pendingSessionFirstLine)
                || LineIndexer.IsCompleted)
            {
                pendingSessionFirstLine = -1; // both targets are now indexed; stop nudging
            }
        }

        // Toggles whether the last file is reopened on startup (issue #51). Turning it off forgets the remembered
        // file immediately (a privacy-friendly, well-known opt-out).
        private void ReopenLast_Click(object sender, RoutedEventArgs e)
        {
            settings.RestoreLastSession = ReopenLastToggle.IsChecked;
            if (!settings.RestoreLastSession)
            {
                ClearSavedSession();
            }
            settingsStore.Save(settings);
        }

        private void ClearSavedSession()
        {
            settings.LastSessionFilePath = "";
            settings.LastSessionFirstVisibleLine = 0;
            settings.LastSessionCaretLine = 0;
            settings.LastSessionCaretColumn = 0;
        }

        // ----- Stdin piping (issue #103): `tool | FujiyNotepad -` -----

        // A windows-subsystem process can still inherit a redirected stdin handle when launched from a pipe.
        // Returns an open standard-input stream only when it is *genuinely* redirected to a pipe/file; a normal
        // launch (no pipe) has a null handle for which OpenStandardInput returns Stream.Null, so we return null
        // there and let the caller fall back to session restore (#51). Probing can throw with no console at all.
        private static Stream? TryOpenRedirectedStdin()
        {
            try
            {
                if (!Console.IsInputRedirected)
                {
                    return null;
                }
                Stream input = Console.OpenStandardInput();
                return ReferenceEquals(input, Stream.Null) ? null : input;
            }
            catch
            {
                return null;
            }
        }

        // Reads piped standard input. A pipe is not seekable and the engine needs random access, so stdin is
        // spooled on a background thread into a temp file that is opened and followed live; the temp file is
        // deleted on close or when another file is opened. <paramref name="input"/> is an already-opened stdin
        // stream (from the auto-detect path); when null (an explicit `-`) it is opened here.
        private async Task OpenFromStdinAsync(Stream? input)
        {
            if (input is null)
            {
                try
                {
                    input = Console.OpenStandardInput();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
                {
                    return; // no usable stdin (e.g. `-` given but launched without a pipe)
                }
            }

            // A null stdin handle yields Stream.Null; bail so an explicit `-` without a pipe doesn't open an
            // empty "<stdin>" window.
            if (ReferenceEquals(input, Stream.Null))
            {
                return;
            }

            string temp = Path.Combine(Path.GetTempPath(), "FujiyNotepad-stdin-" + Guid.NewGuid().ToString("N") + ".log");
            FileStream output;
            try
            {
                // Share Read|Delete so the viewer can open/tail it and it can be deleted while still being written.
                output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                input.Dispose();
                return;
            }

            var cts = new CancellationTokenSource();
            stdinTempPath = temp;
            stdinCts = cts;

            // Spool in the background; the viewer opens immediately and Follow Tail pulls bytes in as they arrive.
            _ = Task.Run(async () =>
            {
                try
                {
                    await StdinSpooler.SpoolAsync(input, output, cts.Token);
                }
                catch
                {
                    // best-effort: cancelled on close/replace, or the producer errored — nothing to recover
                }
                finally
                {
                    await output.DisposeAsync();
                    input.Dispose();
                }
            });

            await OpenFile(temp, addToRecent: false); // temp == stdinTempPath, so this does not clean itself up
            Title = LocalizedStrings.Get("StdinWindowTitle");
            EnableFollowTail(rebaseline: false); // keep the open-time baseline so spooled-in bytes get indexed
        }

        // Cancels stdin spooling and deletes the temp file. Safe to call repeatedly. The file is opened with
        // FileShare.Delete everywhere, so the delete succeeds even while the spool/viewer handles are still open.
        private void CleanupStdin()
        {
            if (stdinTempPath is null)
            {
                return;
            }

            stdinCts?.Cancel();
            stdinCts?.Dispose();
            stdinCts = null;

            try
            {
                File.Delete(stdinTempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best-effort temp cleanup; the OS reclaims %TEMP% eventually
            }

            stdinTempPath = null;
        }

        private async void GoToOffset_Click(object sender, RoutedEventArgs e)
        {
            if (provider is null || source is null)
            {
                return;
            }

            ExitFilterToFullView();
            var input = new TextBox { PlaceholderText = LocalizedStrings.Get("GoToOffsetPlaceholder") };
            var dialog = new ContentDialog
            {
                Title = LocalizedStrings.Get("GoToOffsetTitle"),
                Content = input,
                PrimaryButtonText = LocalizedStrings.Get("DialogGo"),
                CloseButtonText = LocalizedStrings.Get("DialogCancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };

            // Pressing Enter in the box confirms the dialog, the same as clicking "Go".
            bool confirmedByEnter = false;
            input.KeyDown += (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    args.Handled = true;
                    confirmedByEnter = true;
                    dialog.Hide();
                }
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (!(confirmedByEnter || result == ContentDialogResult.Primary) || !OffsetParser.TryParse(input.Text, out long offset))
            {
                return;
            }

            long length = source.Length;
            if (length <= 0)
            {
                return;
            }
            offset = Math.Clamp(offset, 0, length - 1);

            // The offset's line is only known once indexing has reached it; resolving past the indexed frontier
            // would clamp to the last indexed line (the wrong place) and read up to the rest of the file on the
            // UI thread. Ask the user to retry as indexing catches up — the same guard the Find flow uses.
            if (!LineIndexer.CanResolveOffset(offset))
            {
                await ShowMessageAsync("Go To Offset", "That offset hasn't been indexed yet. Try again once indexing has progressed further.");
                return;
            }

            int line = LineIndexer.GetLineNumberFromOffset(offset);
            long lineStart = LineIndexer.GetOffsetFromLineNumber(line + 1);
            int charColumn = provider.ByteColumnToCharColumn(line, offset - lineStart);
            View.GoToLineColumn(line, charColumn);
            View.FocusCanvas();
        }

        private async void GoToPercent_Click(object sender, RoutedEventArgs e)
        {
            if (provider is null || source is null)
            {
                return;
            }

            ExitFilterToFullView();
            var input = new TextBox { PlaceholderText = LocalizedStrings.Get("GoToPercentPlaceholder") };
            var dialog = new ContentDialog
            {
                Title = LocalizedStrings.Get("GoToPercentTitle"),
                Content = input,
                PrimaryButtonText = LocalizedStrings.Get("DialogGo"),
                CloseButtonText = LocalizedStrings.Get("DialogCancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };

            // Pressing Enter in the box confirms the dialog, the same as clicking "Go".
            bool confirmedByEnter = false;
            input.KeyDown += (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    args.Handled = true;
                    confirmedByEnter = true;
                    dialog.Hide();
                }
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (!(confirmedByEnter || result == ContentDialogResult.Primary) || !PercentParser.TryParse(input.Text, out double percent))
            {
                return;
            }

            long length = source.Length;
            if (length <= 0)
            {
                return;
            }
            long offset = PercentParser.ToOffset(percent, length);

            // Same indexed-frontier guard as Go To Offset: the byte position's line is only known once indexing
            // has reached it; resolving past the frontier would land on the wrong (last indexed) line.
            if (!LineIndexer.CanResolveOffset(offset))
            {
                await ShowMessageAsync("Go To Percentage", "That position hasn't been indexed yet. Try again once indexing has progressed further.");
                return;
            }

            int line = LineIndexer.GetLineNumberFromOffset(offset);
            long lineStart = LineIndexer.GetOffsetFromLineNumber(line + 1);
            int charColumn = provider.ByteColumnToCharColumn(line, offset - lineStart);
            View.GoToLineColumn(line, charColumn);
            View.FocusCanvas();
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        private void Find_Click(object sender, RoutedEventArgs e)
        {
            if (provider is null)
            {
                return;
            }

            ExitFilterToFullView();
            findNav = null;
            FindBar.Visibility = Visibility.Visible;
            FindBox.Focus(FocusState.Programmatic);
            FindBox.SelectAll();
            PreviewFind();
        }

        // ----- Bookmarks (toggle on the caret line; jump next/previous, both wrap around) -----

        private void ToggleBookmark_Click(object sender, RoutedEventArgs e)
        {
            if (provider is null)
            {
                return;
            }
            View.ToggleBookmark();
            RefreshMarkerMargin();
            View.FocusCanvas();
        }

        private void NextBookmark_Click(object sender, RoutedEventArgs e)
        {
            if (provider is null)
            {
                return;
            }
            View.GoToNextBookmark();
            View.FocusCanvas();
        }

        private void PreviousBookmark_Click(object sender, RoutedEventArgs e)
        {
            if (provider is null)
            {
                return;
            }
            View.GoToPreviousBookmark();
            View.FocusCanvas();
        }

        private void ClearBookmarks_Click(object sender, RoutedEventArgs e)
        {
            if (provider is null)
            {
                return;
            }
            View.ClearBookmarks();
            RefreshMarkerMargin();
            View.FocusCanvas();
        }

        // ----- Quick clipboard / file actions -----

        private void CopyWithLineNumbers_Click(object sender, RoutedEventArgs e)
        {
            if (provider is null)
            {
                return;
            }
            View.CopySelectionWithLineNumbers();
            View.FocusCanvas();
        }

        private void CopyFilePath_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentFilePath))
            {
                return;
            }
            try
            {
                var package = new DataPackage();
                package.SetText(currentFilePath);
                Clipboard.SetContent(package);
            }
            catch (Exception)
            {
                // Best-effort: the clipboard can be transiently locked by another process.
            }
        }

        private void RevealInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentFilePath) || !File.Exists(currentFilePath))
            {
                return;
            }
            try
            {
                // Open the containing folder with the file selected.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{currentFilePath}\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception)
            {
                // Best-effort: launching Explorer can fail (e.g. policy); never crash the app.
            }
        }

        // Paints the marker overview-ruler strip beside the scrollbar: find-match ticks (orange, under) and
        // bookmark ticks (blue, on top). The strip is narrow and left-pinned (see XAML), so the ticks fill its
        // full width and never reach over the scrollbar thumb (#138).
        private void MarkerMargin_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (provider is null)
            {
                return;
            }

            CanvasDrawingSession ds = args.DrawingSession;
            double height = sender.Size.Height;
            float width = (float)sender.Size.Width;

            // Find-match ticks reflect real-file lines, so suppress them while the filter view is active.
            if (filteredSource is null && matchMarks is { Count: > 0 })
            {
                Windows.UI.Color matchColor = Windows.UI.Color.FromArgb(255, 0xE0, 0x6C, 0x00);
                foreach (int row in ScrollbarMarkers.Rows(matchMarks.Buckets, MatchMarks.Resolution, height))
                {
                    ds.FillRectangle(0f, row, width, 2f, matchColor);
                }
            }

            Windows.UI.Color bookmarkColor = Windows.UI.Color.FromArgb(255, 0x2E, 0x8B, 0xE6);
            foreach (int row in ScrollbarMarkers.Rows(View.BookmarkLines, View.TotalLines, height))
            {
                ds.FillRectangle(0f, row, width, 2f, bookmarkColor);
            }
        }

        private void RefreshMarkerMargin() => MarkerMargin.Invalidate();

        // Drops the find-match ticks (when the find is dismissed / its term cleared) and repaints the margin.
        private void ClearMatchMarks()
        {
            if (matchMarks != null)
            {
                matchMarks = null;
                RefreshMarkerMargin();
            }
        }

        // ----- Filter / grep view (show only matching lines) -----

        private void Filter_Click(object sender, RoutedEventArgs e)
        {
            if (provider is null)
            {
                return;
            }

            filterNav = null;
            FilterBar.Visibility = Visibility.Visible;
            FilterBox.Focus(FocusState.Programmatic);
            FilterBox.SelectAll();
        }

        private async void FilterBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                await ApplyFilter();
            }
            else if (e.Key == Windows.System.VirtualKey.Up)
            {
                e.Handled = true;
                RecallHistory(FilterBox, ref filterNav, settings.RecentFilters, up: true);
            }
            else if (e.Key == Windows.System.VirtualKey.Down)
            {
                e.Handled = true;
                RecallHistory(FilterBox, ref filterNav, settings.RecentFilters, up: false);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                FilterClose_Click(sender, e);
            }
        }

        private async void FilterApply_Click(object sender, RoutedEventArgs e) => await ApplyFilter();

        private void FilterClose_Click(object sender, RoutedEventArgs e)
        {
            ExitFilterToFullView();
            View.FocusCanvas();
        }

        // Builds the per-line predicate from the filter box + options, or null when blank or invalid.
        private Func<string, bool>? BuildFilterPredicate(out string? error)
        {
            error = null;
            string term = FilterBox.Text;
            if (string.IsNullOrEmpty(term))
            {
                return null;
            }

            bool matchCase = FilterMatchCase.IsChecked == true;
            if (FilterRegex.IsChecked == true)
            {
                try
                {
                    var regex = new Regex(term, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                    return line => regex.IsMatch(line);
                }
                catch (ArgumentException)
                {
                    error = "Invalid regex";
                    return null;
                }
            }

            StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return line => line.Contains(term, comparison);
        }

        // Scans the file off the UI thread for lines matching the filter, then swaps the view to render only
        // those lines (a FilteredLineSource). Cancellable and bounded so it stays responsive on huge files.
        private async Task ApplyFilter()
        {
            if (provider is null)
            {
                return;
            }

            Func<string, bool>? predicate = BuildFilterPredicate(out string? error);
            if (error is not null)
            {
                FilterStatus.Text = error;
                return;
            }

            if (predicate is null)
            {
                // A blank term clears the filter back to the full view but keeps the bar open to retype.
                ExitFilterToFullView();
                FilterBar.Visibility = Visibility.Visible;
                return;
            }

            // Remember the applied filter term for Up/Down recall (skip a redundant write for a repeat).
            string filterTerm = FilterBox.Text;
            if (settings.RecentFilters.Count == 0 ||
                !string.Equals(settings.RecentFilters[0], filterTerm, StringComparison.OrdinalIgnoreCase))
            {
                settings.RecentFilters = SearchHistory.Add(settings.RecentFilters, filterTerm);
                settingsStore.Save(settings);
            }
            filterNav = null;

            filterCts?.Cancel();
            var cts = new CancellationTokenSource();
            filterCts = cts;
            CancellationToken token = cts.Token;

            FilterStatus.Text = "Filtering\u2026";
            LineProvider activeProvider = provider;
            var progress = new Progress<int>(p =>
            {
                if (!token.IsCancellationRequested)
                {
                    FilterStatus.Text = $"Filtering\u2026 {p}%";
                }
            });

            List<int> matches;
            bool capped = false;
            try
            {
                matches = await Task.Run(() =>
                {
                    List<int> hits = LineFilter.Match(activeProvider, predicate, out bool c, progress: progress, token: token);
                    capped = c;
                    return hits;
                }, token);
            }
            catch (OperationCanceledException)
            {
                return; // superseded by a newer filter or a clear
            }
            catch (Exception)
            {
                FilterStatus.Text = "Filter failed";
                return;
            }

            // Ignore a stale result (the file changed or another filter started while we scanned).
            if (token.IsCancellationRequested || !ReferenceEquals(provider, activeProvider))
            {
                return;
            }

            filteredSource = new FilteredLineSource(activeProvider, matches);
            View.SetProvider(filteredSource);
            CopyMatchingItem.IsEnabled = true;
            SaveMatchingItem.IsEnabled = true;
            FilterStatus.Text = capped
                ? $"{matches.Count:N0} matching lines (capped)"
                : $"{matches.Count:N0} matching lines";
            LblStatus.Text = StatusText.Filtered(matches.Count, activeProvider.LineCount);
            View.FocusCanvas();
        }

        // Clears the filter state and hides the bar (does not itself restore the view).
        private void ResetFilter()
        {
            filterCts?.Cancel();
            filterCts = null;
            filteredSource = null;
            CopyMatchingItem.IsEnabled = false;
            SaveMatchingItem.IsEnabled = false;
            FilterStatus.Text = string.Empty;
            FilterBar.Visibility = Visibility.Collapsed;
        }

        // Leaves the filter view: clears the filter and restores the full file in the canvas.
        private void ExitFilterToFullView()
        {
            bool wasFiltering = filteredSource is not null;
            ResetFilter();
            if (wasFiltering && provider is not null)
            {
                View.SetProvider(provider);
                LblStatus.Text = StatusText.LineCount(provider.LineCount);
                RefreshMarkerMargin(); // the filter cleared bookmarks, so clear their ticks too
            }
        }

        // Copies just the matching (filtered) lines to the clipboard — the GUI equivalent of piping grep's
        // output. The set can be large, so it is gathered off the UI thread and capped to a bounded string.
        private async void CopyMatchingLines_Click(object sender, RoutedEventArgs e)
        {
            FilteredLineSource? lines = filteredSource;
            if (lines is null)
            {
                return;
            }

            FilterStatus.Text = "Copying\u2026";
            try
            {
                (string text, int lineCount, bool truncated) =
                    await Task.Run(() => MatchingLinesExporter.BuildClipboardText(lines));

                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);

                FilterStatus.Text = truncated
                    ? $"Copied first {lineCount:N0} matching lines (capped)"
                    : $"Copied {lineCount:N0} matching lines";
            }
            catch (Exception)
            {
                // Best-effort: the clipboard can be transiently locked by another process.
                FilterStatus.Text = "Copy failed";
            }
        }

        // Saves the matching (filtered) lines to a file - the GUI equivalent of "grep PATTERN file > out.txt".
        // Streamed and uncapped, in UTF-8, off the UI thread so an arbitrarily large match set stays responsive.
        private async void SaveMatchingLines_Click(object sender, RoutedEventArgs e)
        {
            FilteredLineSource? lines = filteredSource;
            if (lines is null)
            {
                return;
            }

            // Modern Windows App SDK desktop picker: constructed from the window id (no InitializeWithWindow)
            // and Native-AOT safe. The legacy Windows.Storage.Pickers.FileSavePicker can't be used here - its
            // FileTypeChoices requires a managed List<string>, which CsWinRT can't marshal into WinRT under AOT,
            // so the picker throws before it ever shows. We set a default extension instead of FileTypeChoices
            // to avoid handing any managed collection across the WinRT boundary.
            var picker = new FileSavePicker(AppWindow.Id)
            {
                SuggestedFileName = string.IsNullOrEmpty(currentFilePath)
                    ? "filtered-lines"
                    : Path.GetFileNameWithoutExtension(currentFilePath) + "-filtered",
                DefaultFileExtension = ".txt",
            };

            PickFileResult? result = await picker.PickSaveFileAsync();
            if (result is null || string.IsNullOrEmpty(result.Path))
            {
                return;
            }

            string path = result.Path;
            FilterStatus.Text = "Saving\u2026";
            try
            {
                await Task.Run(() =>
                {
                    // UTF-8 without BOM: faithful to the decoded text and broadly compatible, regardless of
                    // the source file's own encoding.
                    using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
                    MatchingLinesExporter.Write(lines, writer);
                });
                FilterStatus.Text = $"Saved {lines.LineCount:N0} matching lines";
            }
            catch (Exception)
            {
                FilterStatus.Text = "Save failed";
            }
        }

        private async void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                if (IsShiftDown())
                {
                    await RunFindPrevious();
                }
                else
                {
                    await RunFindNext();
                }
            }
            else if (e.Key == Windows.System.VirtualKey.Up)
            {
                e.Handled = true;
                RecallHistory(FindBox, ref findNav, settings.RecentSearches, up: true);
            }
            else if (e.Key == Windows.System.VirtualKey.Down)
            {
                e.Handled = true;
                RecallHistory(FindBox, ref findNav, settings.RecentSearches, up: false);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                if (isFinding)
                {
                    findCts?.Cancel();
                }
                else
                {
                    CloseFindBar();
                }
            }
        }

        private static bool IsShiftDown()
            => Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        // Up/Down recall for a search box: walk the MRU history (or back to the in-progress draft) and write the
        // recalled text into the box, caret at the end. A null result means "nothing to recall, leave it as-is".
        private static void RecallHistory(TextBox box, ref SearchHistoryNavigator? nav, List<string> history, bool up)
        {
            nav ??= new SearchHistoryNavigator(history);
            string? recalled = up ? nav.MoveUp(box.Text) : nav.MoveDown(box.Text);
            if (recalled is null)
            {
                return;
            }

            box.Text = recalled;
            box.Select(recalled.Length, 0);
        }

        private void ClearSearchHistory_Click(object sender, RoutedEventArgs e)
        {
            settings.RecentSearches.Clear();
            settings.RecentFilters.Clear();
            findNav = null;
            filterNav = null;
            settingsStore.Save(settings);
        }

        private async void FindNext_Click(object sender, RoutedEventArgs e) => await RunFindNext();

        private async void FindPrev_Click(object sender, RoutedEventArgs e) => await RunFindPrevious();

        // F3 is a window-wide shortcut (like Ctrl+F): open the find bar if it's closed, then repeat the
        // search — or focus the box for input when no term has been entered yet.
        private async void FindNextAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (provider is null)
            {
                return;
            }
            args.Handled = true;

            if (FindBar.Visibility != Visibility.Visible)
            {
                FindBar.Visibility = Visibility.Visible;
            }

            if (string.IsNullOrEmpty(FindBox.Text))
            {
                FindBox.Focus(FocusState.Programmatic);
                FindBox.SelectAll();
            }
            else
            {
                await RunFindNext();
            }
        }

        // Shift+F3: the backward counterpart of F3. Opens the find bar if needed, then repeats the search
        // backward (or focuses the box when no term has been entered).
        private async void FindPreviousAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (provider is null)
            {
                return;
            }
            args.Handled = true;

            if (FindBar.Visibility != Visibility.Visible)
            {
                FindBar.Visibility = Visibility.Visible;
            }

            if (string.IsNullOrEmpty(FindBox.Text))
            {
                FindBox.Focus(FocusState.Programmatic);
                FindBox.SelectAll();
            }
            else
            {
                await RunFindPrevious();
            }
        }

        private void FindCancel_Click(object sender, RoutedEventArgs e) => findCts?.Cancel();

        private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFindBar();

        private void CloseFindBar()
        {
            findPreviewTimer.Stop();
            findCts?.Cancel();
            FindBar.Visibility = Visibility.Collapsed;
            View.SetHighlighter(null);
            ClearMatchMarks();
            View.FocusCanvas();
        }

        // "Find next" reads the option toggles, dispatches to the literal byte search or the line-scoped
        // regex search forward through the document.
        private async Task RunFindNext()
        {
            if (!TryBeginFind(out string text, out bool useRegex, out byte[]? pattern, out SearchOptions options, out Regex? regex))
            {
                return;
            }

            // Find works on real file offsets; if a filter view is active (e.g. F3 with a leftover term), leave it
            // first so the match is selected in the full document rather than against the filtered row set.
            ExitFilterToFullView();

            if (useRegex)
            {
                await RunFindNextRegex(regex!);
            }
            else
            {
                await RunFindNextLiteral(text, pattern!, options);
            }
        }

        // "Find previous" is the backward counterpart of "Find next": same options and match count, but it
        // walks toward the start of the document, anchored above the current selection.
        private async Task RunFindPrevious()
        {
            if (!TryBeginFind(out string text, out bool useRegex, out byte[]? pattern, out SearchOptions options, out Regex? regex))
            {
                return;
            }

            // See RunFindNext: drop the filter view before selecting so the match maps to the full document.
            ExitFilterToFullView();

            if (useRegex)
            {
                await RunFindPreviousRegex(regex!);
            }
            else
            {
                await RunFindPreviousLiteral(text, pattern!, options);
            }
        }

        // Shared Find preamble for both directions: validates the term, reads the option toggles, resets the
        // wrap/caret state on a changed term or a moved caret, builds the literal pattern or regex, and kicks
        // off the (direction-independent) background match count. Returns false when there is nothing to do.
        private bool TryBeginFind(out string text, out bool useRegex, out byte[]? pattern, out SearchOptions options, out Regex? regex)
        {
            text = string.Empty;
            useRegex = false;
            pattern = null;
            options = default;
            regex = null;

            if (provider is null || isFinding)
            {
                return false;
            }

            text = FindBox.Text;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            bool matchCase = MatchCaseToggle.IsChecked == true;
            bool wholeWord = WholeWordToggle.IsChecked == true;
            useRegex = RegexToggle.IsChecked == true;
            string key = $"{useRegex}|{matchCase}|{wholeWord}|{text}";

            // A changed term/options drops any pending wrap, and a moved caret restarts from the caret; the
            // coordinator owns that state machine (headlessly tested in FindCoordinatorTests).
            findCoordinator.Begin(key, View.CaretPosition);

            if (!ApplyFindMatch(text, matchCase, wholeWord, useRegex, key, out pattern, out options, out regex))
            {
                return false;
            }

            // Remember the executed term for Up/Down recall; skip a redundant write when it is already the most
            // recent entry (e.g. repeated F3), then reset the recall walk so the next Up starts fresh.
            if (settings.RecentSearches.Count == 0 ||
                !string.Equals(settings.RecentSearches[0], text, StringComparison.OrdinalIgnoreCase))
            {
                settings.RecentSearches = SearchHistory.Add(settings.RecentSearches, text);
                settingsStore.Save(settings);
            }
            findNav = null;
            return true;
        }

        // Builds the matcher for the term + options, sets the live viewport highlight, and kicks off the
        // background match count + marker margin. Shows the "Invalid regex" state and returns false for a bad
        // regex; otherwise outputs the matcher and returns true. Shared by the incremental preview and Enter/F3.
        private bool ApplyFindMatch(string text, bool matchCase, bool wholeWord, bool useRegex, string key,
            out byte[]? pattern, out SearchOptions options, out Regex? regex)
        {
            pattern = null;
            options = default;
            regex = null;

            if (useRegex)
            {
                try
                {
                    regex = FindRegexBuilder.Build(text, matchCase, wholeWord);
                }
                catch (ArgumentException)
                {
                    FindStatus.Text = "Invalid regex";
                    FindCount.Text = string.Empty;
                    countedKey = null;
                    View.SetHighlighter(null);
                    ClearMatchMarks();
                    return false;
                }
            }
            else
            {
                // Encode the term in the file's encoding and only accept matches on a code-unit boundary, so
                // literal find works in UTF-16/UTF-32 as well as UTF-8/ANSI.
                options = new SearchOptions
                {
                    IgnoreCase = !matchCase,
                    WholeWord = wholeWord,
                    UnitAlignment = currentEncoding.CodeUnitSize,
                    BigEndian = currentEncoding.IsBigEndian,
                };
                pattern = currentEncoding.Encode(text);
            }

            _ = RefreshMatchCountAsync(key, useRegex, pattern, options, regex);

            // Highlight every match of the term in the viewport (the selected match stays distinct).
            View.SetHighlighter(useRegex
                ? new RegexLineHighlighter(regex!)
                : new LiteralLineHighlighter(text, ignoreCase: !matchCase, wholeWord: wholeWord));
            return true;
        }

        // Debounced incremental find (issue #63): each change to the term restarts a short timer that then
        // highlights and counts the term live. Enter / F3 still drive the actual navigation.
        private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            findPreviewTimer.Stop();
            findPreviewTimer.Start();
        }

        // Live-highlights and counts the current term without moving the caret. Honours the option toggles and
        // shows the usual "Invalid regex" state; an empty term clears the highlight, count and marks.
        private void PreviewFind()
        {
            if (provider is null || isFinding)
            {
                return;
            }

            string text = FindBox.Text;
            if (string.IsNullOrEmpty(text))
            {
                countCts?.Cancel();
                countedKey = null;
                FindStatus.Text = string.Empty;
                FindCount.Text = string.Empty;
                View.SetHighlighter(null);
                ClearMatchMarks();
                return;
            }

            bool matchCase = MatchCaseToggle.IsChecked == true;
            bool wholeWord = WholeWordToggle.IsChecked == true;
            bool useRegex = RegexToggle.IsChecked == true;
            string key = $"{useRegex}|{matchCase}|{wholeWord}|{text}";

            FindStatus.Text = string.Empty; // clear any prior "Invalid regex" before (re)building
            ApplyFindMatch(text, matchCase, wholeWord, useRegex, key, out _, out _, out _);
        }

        // Forward literal byte search off the UI thread, honouring the case/whole-word options, progress and
        // cancellation. The coordinator decides where to start (past the last hit, or from the caret when the
        // term changed or the previous search wrapped).
        private async Task RunFindNextLiteral(string text, byte[] pattern, SearchOptions options)
        {
            long start = findCoordinator.PlanLiteralForward(text, GetCaretAnchorOffset());

            // Capture the provider so a concurrent file switch/close can be detected after the search and
            // its (stale) result ignored; the try/catch handles a torn-down read mid-search.
            LineProvider activeProvider = provider!;

            using var cts = new CancellationTokenSource();
            findCts = cts;
            CancellationToken token = cts.Token;
            SetFindBusy(true);
            var progress = new Progress<int>(p => FindProgress.Value = p);

            long? match = await Task.Run(async () =>
            {
                try
                {
                    await foreach (long m in searcher.Search(start, pattern, options, progress, token))
                    {
                        return (long?)m;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // The file was closed/switched while searching; abandon this search.
                }
                return (long?)null;
            });

            SetFindBusy(false);
            findCts = null;

            // If the file changed while searching, the result is stale.
            if (!ReferenceEquals(provider, activeProvider))
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                FindStatus.Text = "Cancelled";
                return;
            }

            if (match.HasValue)
            {
                long matchOffset = match.Value;

                // Index-readiness guard: if the match is beyond the indexed frontier its line isn't known
                // yet, and resolving it would clamp to the wrong line and read up to the rest of the file on
                // the UI thread. Leave the Find state unchanged so the user can retry as indexing catches up.
                if (!LineIndexer.CanResolveOffset(matchOffset))
                {
                    FindStatus.Text = "Past indexed area — retry";
                    return;
                }

                int line = LineIndexer.GetLineNumberFromOffset(matchOffset);
                long lineStart = LineIndexer.GetOffsetFromLineNumber(line + 1);
                int charColumn = provider!.ByteColumnToCharColumn(line, matchOffset - lineStart);
                View.SelectMatch(line, charColumn, text.Length);
                FindStatus.Text = $"Ln {line + 1}";
                findCoordinator.RecordLiteralMatch(matchOffset, pattern.Length, View.CaretPosition);
            }
            else
            {
                // No match through the end of the document; the next Find next wraps back to the start.
                FindStatus.Text = "No matches";
                findCoordinator.RecordLiteralNoMatch(FindDirection.Forward, View.CaretPosition);
            }
        }

        // Forward line-scoped regex search. Only indexed lines are searched, so a match is always resolvable
        // (no indexed-frontier guard needed). Runs off the UI thread; cancellation is checked between lines.
        private async Task RunFindNextRegex(Regex regex)
        {
            (int startLine, int startChar) = findCoordinator.PlanRegexForward(FindBox.Text, GetCaretForFind());
            LineProvider activeProvider = provider!;

            using var cts = new CancellationTokenSource();
            findCts = cts;
            CancellationToken token = cts.Token;
            SetFindBusy(true, indeterminate: true);

            RegexLineSearcher.LineMatch? match = await Task.Run(() =>
            {
                try
                {
                    return new RegexLineSearcher(activeProvider).FindNext(regex, startLine, startChar, token);
                }
                catch (ObjectDisposedException)
                {
                    return (RegexLineSearcher.LineMatch?)null;
                }
            });

            SetFindBusy(false);
            findCts = null;

            if (!ReferenceEquals(provider, activeProvider))
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                FindStatus.Text = "Cancelled";
                return;
            }

            if (match is { } m)
            {
                View.SelectMatch(m.LineIndex, m.CharStart, m.CharLength);
                FindStatus.Text = $"Ln {m.LineIndex + 1}";
                findCoordinator.RecordRegexMatch(m.LineIndex, m.CharStart, m.CharLength, View.CaretPosition);
            }
            else
            {
                // No match through the end of the document; the next Find next wraps back to the start.
                FindStatus.Text = "No matches";
                findCoordinator.RecordRegexNoMatch(FindDirection.Forward, View.CaretPosition);
            }
        }

        // Backward literal byte search off the UI thread. The upper bound is the current selection start
        // (so the current match isn't re-found); after a no-match the next call wraps to the end of the
        // document. Honours the case/whole-word options and the same indexed-frontier guard as Find next.
        private async Task RunFindPreviousLiteral(string text, byte[] pattern, SearchOptions options)
        {
            // Stop just before the current selection, or wrap to the end (long.MaxValue, clamped by FindLastBefore).
            long before = findCoordinator.PlanLiteralBackward(GetSelectionStartOffset());

            LineProvider activeProvider = provider!;

            using var cts = new CancellationTokenSource();
            findCts = cts;
            CancellationToken token = cts.Token;
            SetFindBusy(true, indeterminate: true);

            long? match = await Task.Run(() =>
            {
                try
                {
                    return searcher.FindLastBefore(before, pattern, options, token);
                }
                catch (ObjectDisposedException)
                {
                    return (long?)null;
                }
            });

            SetFindBusy(false);
            findCts = null;

            if (!ReferenceEquals(provider, activeProvider))
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                FindStatus.Text = "Cancelled";
                return;
            }

            if (match.HasValue)
            {
                long matchOffset = match.Value;

                // Index-readiness guard (as in Find next): a match beyond the indexed frontier (only reachable
                // here after wrapping to the end) can't be resolved to a line yet; leave the state for a retry.
                if (!LineIndexer.CanResolveOffset(matchOffset))
                {
                    FindStatus.Text = "Past indexed area — retry";
                    return;
                }

                int line = LineIndexer.GetLineNumberFromOffset(matchOffset);
                long lineStart = LineIndexer.GetOffsetFromLineNumber(line + 1);
                int charColumn = provider!.ByteColumnToCharColumn(line, matchOffset - lineStart);
                View.SelectMatch(line, charColumn, text.Length);
                FindStatus.Text = $"Ln {line + 1}";
                findCoordinator.RecordLiteralMatch(matchOffset, pattern.Length, View.CaretPosition);
            }
            else
            {
                // No match before the caret; the next Find previous wraps back to the end of the document.
                FindStatus.Text = "No matches";
                findCoordinator.RecordLiteralNoMatch(FindDirection.Backward, View.CaretPosition);
            }
        }

        // Backward line-scoped regex search. Anchored just before the current selection start; after a
        // no-match the next call wraps to the end of the document. Only indexed lines are searched.
        private async Task RunFindPreviousRegex(Regex regex)
        {
            // Stop just before the current selection, or wrap to the end (last line, clamped per line by the searcher).
            (int beforeLine, int beforeChar) = findCoordinator.PlanRegexBackward(GetSelectionStartForFind(), provider?.LineCount ?? 1);
            LineProvider activeProvider = provider!;

            using var cts = new CancellationTokenSource();
            findCts = cts;
            CancellationToken token = cts.Token;
            SetFindBusy(true, indeterminate: true);

            RegexLineSearcher.LineMatch? match = await Task.Run(() =>
            {
                try
                {
                    return new RegexLineSearcher(activeProvider).FindPrevious(regex, beforeLine, beforeChar, token);
                }
                catch (ObjectDisposedException)
                {
                    return (RegexLineSearcher.LineMatch?)null;
                }
            });

            SetFindBusy(false);
            findCts = null;

            if (!ReferenceEquals(provider, activeProvider))
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                FindStatus.Text = "Cancelled";
                return;
            }

            if (match is { } m)
            {
                View.SelectMatch(m.LineIndex, m.CharStart, m.CharLength);
                FindStatus.Text = $"Ln {m.LineIndex + 1}";
                findCoordinator.RecordRegexMatch(m.LineIndex, m.CharStart, m.CharLength, View.CaretPosition);
            }
            else
            {
                // No match before the caret; the next Find previous wraps back to the end of the document.
                FindStatus.Text = "No matches";
                findCoordinator.RecordRegexNoMatch(FindDirection.Backward, View.CaretPosition);
            }
        }

        // Recomputes the total match count in the background whenever the term/options change, updating the
        // count label. Each request cancels the previous one, and a result for a superseded key is dropped.
        private async Task RefreshMatchCountAsync(string key, bool useRegex, byte[]? pattern, SearchOptions options, Regex? regex)
        {
            if (string.Equals(key, countedKey, StringComparison.Ordinal))
            {
                return;
            }
            countedKey = key;
            countCts?.Cancel();
            using var cts = new CancellationTokenSource();
            countCts = cts;
            CancellationToken token = cts.Token;
            LineProvider activeProvider = provider!;
            var marks = new MatchMarks(activeProvider.LineCount);
            FindCount.Text = LocalizedStrings.Get("StatusCounting");

            int count;
            try
            {
                count = await Task.Run(async () =>
                {
                    try
                    {
                        if (useRegex)
                        {
                            return new RegexLineSearcher(activeProvider).CountAll(regex!, null, token,
                                line => { if (!marks.IsFull) marks.Add(line); });
                        }

                        int n = 0;
                        await foreach (long offset in searcher.Search(0, pattern!, options, null, token))
                        {
                            n++;
                            if (!marks.IsFull)
                            {
                                marks.Add(LineIndexer.GetLineNumberFromOffset(offset));
                            }
                        }
                        return n;
                    }
                    catch (ObjectDisposedException)
                    {
                        return -1;
                    }
                });
            }
            catch (Exception)
            {
                // Fire-and-forget UI counter: never let an unexpected failure escape and crash the process.
                if (ReferenceEquals(countCts, cts)) countCts = null;
                FindCount.Text = string.Empty;
                matchMarks = null;
                return;
            }

            if (ReferenceEquals(countCts, cts))
            {
                countCts = null;
            }
            if (token.IsCancellationRequested || !ReferenceEquals(provider, activeProvider)
                || !string.Equals(key, countedKey, StringComparison.Ordinal))
            {
                return;
            }

            FindCount.Text = count < 0 ? string.Empty : count == 1 ? "1 match" : $"{count:N0} matches";
            matchMarks = count < 0 ? null : marks;
            RefreshMarkerMargin();
        }

        // Persists an option change and invalidates the current find position and cached count, but does NOT
        // search or recount - that waits until the user runs Find next with the new options. Silenced while
        // the saved options are applied at startup.
        private void FindOption_Toggled(object sender, RoutedEventArgs e)
        {
            if (suppressFindOptionEvents)
            {
                return;
            }

            settings.FindMatchCase = MatchCaseToggle.IsChecked == true;
            settings.FindWholeWord = WholeWordToggle.IsChecked == true;
            settings.FindUseRegex = RegexToggle.IsChecked == true;
            settingsStore.Save(settings);

            // Re-run the incremental preview so the live highlight and count reflect the new option at once
            // (an empty term just clears them). Enter / F3 still navigate with the new option.
            findCoordinator.Reset();
            PreviewFind();
        }

        private void SetFindBusy(bool busy, bool indeterminate = false)
        {
            isFinding = busy;
            FindNextButton.IsEnabled = !busy;
            FindPrevButton.IsEnabled = !busy;
            FindCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            FindProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            FindProgress.IsIndeterminate = busy && indeterminate;
            if (busy)
            {
                if (!indeterminate)
                {
                    FindProgress.Value = 0;
                }
                FindStatus.Text = "Searching\u2026";
            }
        }

        private TextPosition GetCaretForFind()
        {
            TextPosition caret = View.CaretPosition;
            return (provider != null && caret.Line >= 0 && caret.Line < provider.LineCount) ? caret : new TextPosition(0, 0);
        }

        private long GetCaretAnchorOffset()
        {
            if (provider == null)
            {
                return 0;
            }

            TextPosition caret = GetCaretForFind();
            try
            {
                long lineStart = LineIndexer.GetOffsetFromLineNumber(caret.Line + 1);
                return lineStart + provider.CharColumnToByteColumn(caret.Line, caret.Column);
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }

        // The start of the current selection (the match start after a Find, or the caret when nothing is
        // selected), used by Find previous as the exclusive upper bound so it never re-finds the current match.
        private TextPosition GetSelectionStartForFind()
        {
            TextPosition start = View.SelectionStart;
            return (provider != null && start.Line >= 0 && start.Line < provider.LineCount) ? start : new TextPosition(0, 0);
        }

        private long GetSelectionStartOffset()
        {
            if (provider == null)
            {
                return 0;
            }

            TextPosition start = GetSelectionStartForFind();
            try
            {
                long lineStart = LineIndexer.GetOffsetFromLineNumber(start.Line + 1);
                return lineStart + provider.CharColumnToByteColumn(start.Line, start.Column);
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        private async void About_Click(object sender, RoutedEventArgs e)
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock
            {
                Text = "FujiyNotepad",
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            panel.Children.Add(new TextBlock { Text = LocalizedStrings.Format("AboutVersion", GetAppVersion()) });
            panel.Children.Add(new TextBlock
            {
                Text = LocalizedStrings.Get("AboutDescription"),
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new HyperlinkButton
            {
                Content = "github.com/felipepessoto/FujiyNotepad",
                NavigateUri = new Uri("https://github.com/felipepessoto/FujiyNotepad"),
                Padding = new Thickness(0),
            });
            panel.Children.Add(new TextBlock
            {
                Text = LocalizedStrings.Get("AboutLicense"),
                FontSize = 12,
                Opacity = 0.7,
            });

            var dialog = new ContentDialog
            {
                Title = LocalizedStrings.Get("AboutTitle"),
                Content = panel,
                CloseButtonText = LocalizedStrings.Get("DialogClose"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        // The version stamped at build time (-p:Version), read from the assembly; the source-revision "+hash"
        // suffix on the informational version is trimmed. Falls back to the assembly version for a dev build.
        private static string GetAppVersion()
        {
            Assembly assembly = typeof(MainWindow).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                int plus = informational.IndexOf('+');
                return plus >= 0 ? informational[..plus] : informational;
            }
            Version? version = assembly.GetName().Version;
            return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        private void TabWidth_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioMenuFlyoutItem item && int.TryParse(item.Tag?.ToString(), out int width))
            {
                View.TabSize = width;
                settings.TabWidth = width;
                settingsStore.Save(settings);
            }
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => View.ZoomIn();

        private void ZoomOut_Click(object sender, RoutedEventArgs e) => View.ZoomOut();

        private void ResetZoom_Click(object sender, RoutedEventArgs e) => View.ResetZoom();

        private void LineNumbers_Click(object sender, RoutedEventArgs e)
        {
            View.ShowLineNumbers = LineNumbersToggle.IsChecked;
            settings.ShowLineNumbers = LineNumbersToggle.IsChecked;
            settingsStore.Save(settings);
        }

        private void Whitespace_Click(object sender, RoutedEventArgs e)
        {
            View.ShowWhitespace = WhitespaceToggle.IsChecked;
            settings.ShowWhitespace = WhitespaceToggle.IsChecked;
            settingsStore.Save(settings);
            View.FocusCanvas();
        }

        private void WordWrap_Click(object sender, RoutedEventArgs e)
        {
            View.WordWrap = WordWrapToggle.IsChecked;
            settings.WordWrap = WordWrapToggle.IsChecked;
            settingsStore.Save(settings);
            UpdateScrollBarVisibility();
            SyncScrollBars();
            View.FocusCanvas();
        }

        private void HighlightSelection_Click(object sender, RoutedEventArgs e)
        {
            settings.HighlightSelectionOccurrences = HighlightSelectionToggle.IsChecked;
            settingsStore.Save(settings);
            UpdateSelectionHighlight();
            View.FocusCanvas();
        }

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioMenuFlyoutItem item && item.Tag is string theme)
            {
                settings.Theme = theme;
                settingsStore.Save(settings);
                ApplyThemeSetting(theme);
                View.FocusCanvas();
            }
        }

        // Applies the theme override to the window's root element (System = follow the OS). The text canvas
        // repaints from its resolved ActualTheme, which RequestedTheme drives, so the whole UI re-themes.
        private void ApplyThemeSetting(string theme)
        {
            RootGrid.RequestedTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        // Parses the persisted rules text and pushes the compiled rule set to the view (null when there are
        // none, so the engine skips the per-line work). Called on startup and whenever the rules are edited.
        private void ApplyHighlightRules()
        {
            List<HighlightRule> rules = HighlightRuleText.Parse(settings.HighlightRulesText);
            View.SetHighlightRules(rules.Count > 0 ? HighlightRuleSet.Build(rules) : null);
        }

        // View > Highlight Rules...: edit the persistent, per-pattern highlight rules as text.
        private async void HighlightRules_Click(object sender, RoutedEventArgs e)
        {
            string stored = string.IsNullOrEmpty(settings.HighlightRulesText)
                ? HighlightRuleText.DefaultExample
                : settings.HighlightRulesText;
            // Normalize to "\n" (defensive against any stored "\r"); the TextBox renders "\n" breaks fine once
            // it is multi-line, which it is because Text is assigned after AcceptsReturn below.
            string editorText = stored.Replace("\r\n", "\n").Replace('\r', '\n');

            var editor = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                Width = 460,
                Height = 240,
            };
            // Assign Text AFTER AcceptsReturn is true: a single-line TextBox (the default) truncates a
            // multi-line value at the first line break when it is assigned, and enabling AcceptsReturn
            // afterward cannot recover the lost lines (verified via UI automation).
            editor.Text = editorText;
            ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);

            var help = new TextBlock
            {
                Text = LocalizedStrings.Get("HighlightRulesHelp"),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                Margin = new Thickness(0, 0, 0, 8),
            };

            var presetMenu = new MenuFlyout();
            foreach (HighlightPreset preset in HighlightPresets.All)
            {
                var item = new MenuFlyoutItem { Text = preset.Name };
                item.Click += (_, _) =>
                {
                    // Append the preset's rules (non-destructive and composable); the user then edits and applies.
                    string current = editor.Text.Replace("\r\n", "\n").Replace('\r', '\n');
                    string sep = current.Length > 0 && !current.EndsWith('\n') ? "\n" : "";
                    editor.Text = current + sep + preset.RulesText;
                };
                presetMenu.Items.Add(item);
            }
            var presetButton = new DropDownButton
            {
                Content = LocalizedStrings.Get("InsertPresetText"),
                Flyout = presetMenu,
                Margin = new Thickness(0, 0, 0, 8),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(presetButton, "InsertPresetButton");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(presetButton, LocalizedStrings.Get("InsertPresetName"));

            var panel = new StackPanel();
            panel.Children.Add(help);
            panel.Children.Add(presetButton);
            panel.Children.Add(editor);

            var dialog = new ContentDialog
            {
                Title = LocalizedStrings.Get("HighlightRulesTitle"),
                Content = panel,
                PrimaryButtonText = LocalizedStrings.Get("DialogApply"),
                CloseButtonText = LocalizedStrings.Get("DialogCancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                // WinUI's multiline TextBox returns '\r' line breaks; normalize to '\n' so the stored text and
                // the dialog round-trip (and the parser) stay consistent.
                settings.HighlightRulesText = editor.Text.Replace("\r\n", "\n").Replace('\r', '\n');
                settingsStore.Save(settings);
                ApplyHighlightRules();
                View.FocusCanvas();
            }
        }

        private void Font_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioMenuFlyoutItem item && item.Tag is string family)
            {
                View.FontFamilyName = family;
            }
        }

        // A font-family or zoom (size) change: show the zoom percent, keep the Font menu ticked, and persist.
        private void OnFontChanged()
        {
            LblZoom.Text = $"{View.ZoomPercent}%";
            TickFontMenu(View.FontFamilyName);
            settings.FontFamily = View.FontFamilyName;
            settings.FontSize = View.FontSizePoints;
            settingsStore.Save(settings);
        }

        private void TickFontMenu(string family)
        {
            FontConsolas.IsChecked = family == "Consolas";
            FontCascadiaMono.IsChecked = family == "Cascadia Mono";
            FontCascadiaCode.IsChecked = family == "Cascadia Code";
            FontCourierNew.IsChecked = family == "Courier New";
            FontLucidaConsole.IsChecked = family == "Lucida Console";
        }

        // Counts the file's total characters in the background (constant memory) and shows it in the status
        // bar. Each call cancels the previous one; a result for a superseded file/encoding is dropped.
        // Decides how to show the character count when a file opens or its encoding changes (issue #39):
        //  - no file -> blank;
        //  - single-byte encoding -> the count equals the byte size, shown instantly with no decode;
        //  - very large file -> defer: show the byte size and offer an on-demand "Count characters" action;
        //  - otherwise -> count automatically in the background as before.
        private async Task RefreshCharacterCountAsync()
        {
            CountCharsLink.Visibility = Visibility.Collapsed;

            if (source is null)
            {
                charCountCts?.Cancel();
                LblCharCount.Text = string.Empty;
                return;
            }

            long length = source.Length;

            if (currentEncoding.Encoding.IsSingleByte)
            {
                charCountCts?.Cancel(); // supersede any in-flight count from a previous encoding
                LblCharCount.Text = StatusText.CharacterCount(length);
                return;
            }

            if (length > AutoCharCountByteLimit)
            {
                charCountCts?.Cancel();
                LblCharCount.Text = StatusText.FileSize(length);
                CountCharsLink.Visibility = Visibility.Visible;
                return;
            }

            await CountCharactersAsync();
        }

        // Runs the actual full-file character count in the background (constant memory, cancellable), updating
        // the status bar. Used by the automatic path for normal-sized files and by the on-demand link for large
        // ones. A stale result (the source or encoding changed meanwhile) is discarded.
        private async Task CountCharactersAsync()
        {
            if (source is null)
            {
                LblCharCount.Text = string.Empty;
                return;
            }

            charCountCts?.Cancel();
            using var cts = new CancellationTokenSource();
            charCountCts = cts;
            CancellationToken token = cts.Token;
            IByteSource activeSource = source;
            TextEncoding encoding = currentEncoding;
            LblCharCount.Text = LocalizedStrings.Get("StatusCounting");

            long count;
            try
            {
                count = await Task.Run(async () =>
                {
                    try
                    {
                        return await CharacterCounter.CountAsync(activeSource, encoding, null, token);
                    }
                    catch (ObjectDisposedException)
                    {
                        return -1L;
                    }
                });
            }
            catch (Exception)
            {
                // Fire-and-forget UI counter: never let an unexpected failure escape and crash the process.
                if (ReferenceEquals(charCountCts, cts)) charCountCts = null;
                LblCharCount.Text = string.Empty;
                return;
            }

            if (ReferenceEquals(charCountCts, cts))
            {
                charCountCts = null;
            }
            if (token.IsCancellationRequested || !ReferenceEquals(source, activeSource))
            {
                return;
            }

            LblCharCount.Text = StatusText.CharacterCount(count);
        }

        // On-demand character count for a large file whose automatic count was deferred (issue #39).
        private async void CountChars_Click(object sender, RoutedEventArgs e)
        {
            CountCharsLink.Visibility = Visibility.Collapsed;
            await CountCharactersAsync();
        }

        // Apply persisted settings on startup: tab width, the saved window size/maximized state, and the
        // recent-files menu.
        private void ApplyStartupSettings()
        {
            int tab = settings.TabWidth is 2 or 4 or 8 ? settings.TabWidth : 4;
            View.TabSize = tab;
            (tab switch { 2 => TabWidth2, 8 => TabWidth8, _ => TabWidth4 }).IsChecked = true;

            // Font + zoom. (FontChanged isn't subscribed yet, so set the zoom label and Font menu directly.)
            View.FontFamilyName = settings.FontFamily;
            View.FontSizePoints = settings.FontSize;
            TickFontMenu(View.FontFamilyName);
            LblZoom.Text = $"{View.ZoomPercent}%";

            LineNumbersToggle.IsChecked = settings.ShowLineNumbers;
            View.ShowLineNumbers = settings.ShowLineNumbers;

            ReopenLastToggle.IsChecked = settings.RestoreLastSession;

            WhitespaceToggle.IsChecked = settings.ShowWhitespace;
            View.ShowWhitespace = settings.ShowWhitespace;

            WordWrapToggle.IsChecked = settings.WordWrap;
            View.WordWrap = settings.WordWrap;
            UpdateScrollBarVisibility();

            HighlightSelectionToggle.IsChecked = settings.HighlightSelectionOccurrences;

            string theme = settings.Theme is "Light" or "Dark" ? settings.Theme : "System";
            ThemeSystem.IsChecked = theme == "System";
            ThemeLight.IsChecked = theme == "Light";
            ThemeDark.IsChecked = theme == "Dark";
            ApplyThemeSetting(theme);

            ApplyHighlightRules();

            suppressFindOptionEvents = true;
            MatchCaseToggle.IsChecked = settings.FindMatchCase;
            WholeWordToggle.IsChecked = settings.FindWholeWord;
            RegexToggle.IsChecked = settings.FindUseRegex;
            suppressFindOptionEvents = false;

            if (settings.WindowWidth >= 320 && settings.WindowHeight >= 240)
            {
                AppWindow.Resize(new SizeInt32(settings.WindowWidth, settings.WindowHeight));
            }
            lastNormalSize = AppWindow.Size;

            if (settings.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }

            // Track the most recent non-maximized size so we restore to it, not the maximized bounds.
            AppWindow.Changed += (s, args) =>
            {
                if (args.DidSizeChange &&
                    s.Presenter is OverlappedPresenter p &&
                    p.State != OverlappedPresenterState.Maximized)
                {
                    lastNormalSize = s.Size;
                }
            };

            RebuildRecentMenu();
        }

        private void SaveWindowState()
        {
            bool maximized = AppWindow.Presenter is OverlappedPresenter p &&
                             p.State == OverlappedPresenterState.Maximized;
            settings.WindowMaximized = maximized;

            SizeInt32 size = maximized ? lastNormalSize : AppWindow.Size;
            if (size.Width > 0 && size.Height > 0)
            {
                settings.WindowWidth = size.Width;
                settings.WindowHeight = size.Height;
            }

            SaveSessionState();
            settingsStore.Save(settings);
        }

        // Remembers the open file and its scroll/caret position for next launch (issue #51), or clears it when
        // session restore is off or there is nothing real to restore (a piped stdin temp file is deleted on
        // close, and a filter view's line numbers don't map to the real file, so those keep position 0).
        private void SaveSessionState()
        {
            bool hasRealFile = currentFilePath is { } path
                && (stdinTempPath is null || !string.Equals(path, stdinTempPath, StringComparison.OrdinalIgnoreCase));

            if (!settings.RestoreLastSession || !hasRealFile)
            {
                ClearSavedSession();
                return;
            }

            settings.LastSessionFilePath = currentFilePath!;
            if (filteredSource is null && provider is not null)
            {
                settings.LastSessionFirstVisibleLine = View.FirstVisibleLine;
                settings.LastSessionCaretLine = View.CaretPosition.Line;
                settings.LastSessionCaretColumn = View.CaretPosition.Column;
            }
            else
            {
                settings.LastSessionFirstVisibleLine = 0;
                settings.LastSessionCaretLine = 0;
                settings.LastSessionCaretColumn = 0;
            }
        }

        // Rebuild the File > Open Recent submenu from the saved list, dropping entries that no longer exist.
        private void RebuildRecentMenu()
        {
            RecentMenu.Items.Clear();
            settings.RecentFiles = RecentFiles.Prune(settings.RecentFiles, File.Exists);

            if (settings.RecentFiles.Count == 0)
            {
                RecentMenu.Items.Add(new MenuFlyoutItem { Text = "(No recent files)", IsEnabled = false });
                return;
            }

            foreach (string path in settings.RecentFiles)
            {
                var item = new MenuFlyoutItem { Text = Path.GetFileName(path) };
                ToolTipService.SetToolTip(item, path);
                item.Click += (_, _) => { _ = OpenFile(path); };
                RecentMenu.Items.Add(item);
            }

            RecentMenu.Items.Add(new MenuFlyoutSeparator());
            var clear = new MenuFlyoutItem { Text = "Clear Recently Opened" };
            clear.Click += (_, _) =>
            {
                settings.RecentFiles.Clear();
                settingsStore.Save(settings);
                RebuildRecentMenu();
            };
            RecentMenu.Items.Add(clear);
        }
    }
}
