using System.Text;
using FujiyNotepad.Core;
using FujiyNotepad.WinUI.Controls;
using FujiyNotepad.WinUI.Logic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;

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

        // Find state: the controller decides where each forward "find next" starts; findCts cancels an
        // in-progress search; isFinding guards against starting a second search while one runs.
        private readonly FindController find = new();
        private CancellationTokenSource? findCts;
        private bool isFinding;

        public MainWindow()
        {
            this.InitializeComponent();
            Title = "Fujiy Notepad (WinUI)";

            View.ViewChanged += SyncScrollBars;
            View.CaretChanged += pos => LblCursor.Text = $"Ln {pos.Line + 1}, Col {pos.Column + 1}";

            indexRefreshTimer = DispatcherQueue.CreateTimer();
            indexRefreshTimer.Interval = TimeSpan.FromMilliseconds(150);
            indexRefreshTimer.Tick += IndexRefreshTimer_Tick;

            Closed += (_, _) => { cancelIndexing?.Cancel(); findCts?.Cancel(); source?.Dispose(); };

            // Open a file passed on the command line (file association / "open with" / drag-onto-exe).
            string? fileArg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
            if (fileArg != null)
            {
                DispatcherQueue.TryEnqueue(() => { _ = OpenFile(fileArg); });
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
            string path = Path.Combine(Path.GetTempPath(), "FujiyNotepadSample.txt");
            if (!File.Exists(path))
            {
                LblStatus.Text = "Generating sample...";
                await Task.Run(() => CreateSampleFile(path));
            }
            await OpenFile(path);
        }

        private static void CreateSampleFile(string path)
        {
            using var writer = new StreamWriter(path);
            var random = new Random(1);
            for (int i = 1; i <= 10_000_000; i++)
            {
                writer.Write(i);
                writer.Write(" - ");
                writer.WriteLine(new string('x', random.Next(300, 500)));
            }
        }

        private async Task OpenFile(string path)
        {
            await StopIndexingAsync();

            source?.Dispose();
            source = new FileByteSource(path);
            searcher = new TextSearcher(source);
            LineIndexer = new LineIndexer(searcher);
            provider = new LineProvider(source, LineIndexer);
            find.Reset();

            View.SetProvider(provider);
            EditMenu.IsEnabled = true;
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
                XamlRoot = Content.XamlRoot,
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && int.TryParse(input.Text, out int line))
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
                await RunFindNext();
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

        private async void FindNext_Click(object sender, RoutedEventArgs e) => await RunFindNext();

        private void FindCancel_Click(object sender, RoutedEventArgs e) => findCts?.Cancel();

        private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFindBar();

        private void CloseFindBar()
        {
            findCts?.Cancel();
            FindBar.Visibility = Visibility.Collapsed;
            View.FocusCanvas();
        }

        // Runs a forward "find next" off the UI thread, reporting progress and honouring cancellation. The
        // FindController decides where to start (past the last hit, or from the caret when the term changed
        // or the previous search wrapped).
        private async Task RunFindNext()
        {
            if (provider is null || isFinding)
            {
                return;
            }

            string text = FindBox.Text;
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            long start = find.PrepareForwardSearch(text, GetCaretAnchorOffset());
            byte[] pattern = Encoding.UTF8.GetBytes(text);

            // Capture the provider so a concurrent file switch/close can be detected after the search and
            // its (stale) result ignored; the try/catch handles a torn-down read mid-search.
            LineProvider activeProvider = provider;

            using var cts = new CancellationTokenSource();
            findCts = cts;
            CancellationToken token = cts.Token;
            SetFindBusy(true);
            var progress = new Progress<int>(p => FindProgress.Value = p);

            long? match = await Task.Run(async () =>
            {
                try
                {
                    await foreach (long m in searcher.Search(start, pattern, progress, token))
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
                int line = LineIndexer.GetLineNumberFromOffset(match.Value);
                long lineStart = LineIndexer.GetOffsetFromLineNumber(line + 1);
                int charColumn = provider!.ByteColumnToCharColumn(line, match.Value - lineStart);
                find.RecordMatch(match.Value);
                View.SelectMatch(line, charColumn, text.Length);
                FindStatus.Text = $"Ln {line + 1}";
            }
            else
            {
                // No match from here on; let the next search wrap to the caret again.
                find.RecordNoMatch();
                FindStatus.Text = "No matches";
            }
        }

        private void SetFindBusy(bool busy)
        {
            isFinding = busy;
            FindNextButton.IsEnabled = !busy;
            FindCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            FindProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy)
            {
                FindProgress.Value = 0;
                FindStatus.Text = "Searching\u2026";
            }
        }

        private long GetCaretAnchorOffset()
        {
            TextPosition caret = View.CaretPosition;
            if (provider != null && caret.Line >= 0 && caret.Line < provider.LineCount)
            {
                try
                {
                    return LineIndexer.GetOffsetFromLineNumber(caret.Line + 1);
                }
                catch (InvalidOperationException)
                {
                    return 0;
                }
            }

            return 0;
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    }
}
