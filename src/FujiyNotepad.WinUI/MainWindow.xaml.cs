using System.Text;
using System.Text.RegularExpressions;
using FujiyNotepad.Core;
using FujiyNotepad.WinUI.Controls;
using FujiyNotepad.WinUI.Logic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace FujiyNotepad.WinUI
{
    public sealed partial class MainWindow : Window
    {
        private IByteSource? source;
        private TextSearcher searcher = null!;
        private LineProvider? provider;
        private LineIndexer LineIndexer = null!;

        private readonly DispatcherQueueTimer indexRefreshTimer;
        private CancellationTokenSource? cancelIndexing;
        private Task indexingTask = Task.CompletedTask;
        private bool syncingScroll;

        private readonly SettingsStore settingsStore = SettingsStore.Default();
        private AppSettings settings = new();
        private SizeInt32 lastNormalSize;

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
            View.CaretChanged += pos => LblCursor.Text = $"Ln {pos.Line + 1}, Col {pos.Column + 1}";

            indexRefreshTimer = DispatcherQueue.CreateTimer();
            indexRefreshTimer.Interval = TimeSpan.FromMilliseconds(150);
            indexRefreshTimer.Tick += IndexRefreshTimer_Tick;

            Closed += (_, _) =>
            {
                SaveWindowState();
                cancelIndexing?.Cancel();
                findCts?.Cancel();
                source?.Dispose();
            };

            // Open a file passed on the command line (file association / "open with" / drag-onto-exe).
            string? fileArg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
            if (fileArg != null)
            {
                DispatcherQueue.TryEnqueue(() => { _ = OpenFile(fileArg); });
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
            var picker = new FileOpenPicker();
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await OpenFile(file.Path);
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
                "ERROR  error  Error  -  the same word in three different cases.",
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
                "-----------------------------------------------------------------------------------",
                "Below: 10,000,000 generated lines (large-file demo).",
            };
        }

        private async Task OpenFile(string path, bool addToRecent = true)
        {
            await StopIndexingAsync();

            source?.Dispose();
            source = new FileByteSource(path);
            searcher = new TextSearcher(source);
            LineIndexer = new LineIndexer(searcher);
            provider = new LineProvider(source, LineIndexer);
            findCoordinator.Reset();
            countCts?.Cancel();
            countedKey = null;
            FindCount.Text = string.Empty;

            View.SetProvider(provider);
            Title = $"{Path.GetFileName(path)} - Fujiy Notepad";
            EditMenu.IsEnabled = true;

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

        private void StartIndexing()
        {
            StartIndexingItem.IsEnabled = false;
            StopIndexingItem.IsEnabled = true;
            cancelIndexing = new CancellationTokenSource();
            CancellationToken token = cancelIndexing.Token;
            var progress = new Progress<int>(p => LblStatus.Text = $"{p}% indexed");
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

            View.UpdateTotalLines(provider.LineCount);

            if (LineIndexer.IsCompleted)
            {
                indexRefreshTimer.Stop();
                StopIndexingItem.IsEnabled = false;
                LblStatus.Text = $"{provider.LineCount:N0} lines";
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
                LblStatus.Text = $"{provider.LineCount:N0} lines (indexing stopped)";
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

            var input = new TextBox { PlaceholderText = "Line number" };
            var dialog = new ContentDialog
            {
                Title = "Go To Line",
                Content = input,
                PrimaryButtonText = "Go",
                CloseButtonText = "Cancel",
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

        private void Find_Click(object sender, RoutedEventArgs e)
        {
            if (provider is null)
            {
                return;
            }

            FindBar.Visibility = Visibility.Visible;
            FindBox.Focus(FocusState.Programmatic);
            FindBox.SelectAll();
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
            findCts?.Cancel();
            FindBar.Visibility = Visibility.Collapsed;
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

            if (useRegex)
            {
                try
                {
                    regex = BuildRegex(text, matchCase, wholeWord);
                }
                catch (ArgumentException)
                {
                    FindStatus.Text = "Invalid regex";
                    FindCount.Text = string.Empty;
                    countedKey = null;
                    return false;
                }
            }
            else
            {
                options = new SearchOptions { IgnoreCase = !matchCase, WholeWord = wholeWord };
                pattern = Encoding.UTF8.GetBytes(text);
            }

            RefreshMatchCount(key, useRegex, pattern, options, regex);
            return true;
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

        // Builds the per-line regex for the current options: whole-word wraps the term in \b...\b, and
        // match-case off adds IgnoreCase. Throws ArgumentException for an invalid pattern.
        private static Regex BuildRegex(string text, bool matchCase, bool wholeWord)
        {
            RegexOptions options = RegexOptions.CultureInvariant;
            if (!matchCase)
            {
                options |= RegexOptions.IgnoreCase;
            }
            string pattern = wholeWord ? $@"\b(?:{text})\b" : text;
            return new Regex(pattern, options);
        }

        // Recomputes the total match count in the background whenever the term/options change, updating the
        // count label. Each request cancels the previous one, and a result for a superseded key is dropped.
        private async void RefreshMatchCount(string key, bool useRegex, byte[]? pattern, SearchOptions options, Regex? regex)
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
            FindCount.Text = "counting\u2026";

            int count = await Task.Run(async () =>
            {
                try
                {
                    if (useRegex)
                    {
                        return new RegexLineSearcher(activeProvider).CountAll(regex!, null, token);
                    }

                    int n = 0;
                    await foreach (long _ in searcher.Search(0, pattern!, options, null, token))
                    {
                        n++;
                    }
                    return n;
                }
                catch (ObjectDisposedException)
                {
                    return -1;
                }
            });

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

            findCoordinator.Reset();
            countCts?.Cancel();
            countedKey = null;
            FindCount.Text = string.Empty;
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

        private void TabWidth_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioMenuFlyoutItem item && int.TryParse(item.Tag?.ToString(), out int width))
            {
                View.TabSize = width;
                settings.TabWidth = width;
                settingsStore.Save(settings);
            }
        }

        // Apply persisted settings on startup: tab width, the saved window size/maximized state, and the
        // recent-files menu.
        private void ApplyStartupSettings()
        {
            int tab = settings.TabWidth is 2 or 4 or 8 ? settings.TabWidth : 4;
            View.TabSize = tab;
            (tab switch { 2 => TabWidth2, 8 => TabWidth8, _ => TabWidth4 }).IsChecked = true;

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

            settingsStore.Save(settings);
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
