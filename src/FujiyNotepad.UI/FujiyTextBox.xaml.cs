using FujiyNotepad.UI.Controls;
using FujiyNotepad.Core;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace FujiyNotepad.UI
{
    public partial class FujiyTextBox : UserControl
    {
        IByteSource? source;
        TextSearcher searcher = null!;
        LineProvider? provider;

        readonly DispatcherTimer indexRefreshTimer;
        bool syncingScroll;

        // Where the next Find should start. -1 means "no previous match", so the search anchors on the
        // caret instead; after a hit it advances past that match so repeated Find walks occurrences.
        long lastFoundOffset = -1;

        public LineIndexer LineIndexer { get; private set; } = null!;

        /// <summary>Raised with the 1-based caret line and column whenever the caret moves.</summary>
        public event Action<int, int>? CaretPositionChanged;

        public FujiyTextBox()
        {
            InitializeComponent();
            IsEnabled = false;

            View.ViewChanged += SyncScrollBars;
            View.CaretChanged += pos => CaretPositionChanged?.Invoke(pos.Line + 1, pos.Column + 1);

            indexRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            indexRefreshTimer.Tick += IndexRefreshTimer_Tick;
        }

        public Task OpenFile(string filePath)
        {
            IsEnabled = true;
            indexRefreshTimer.Stop();
            source?.Dispose();

            source = new FileByteSource(filePath);
            searcher = new TextSearcher(source);
            LineIndexer = new LineIndexer(searcher);
            provider = new LineProvider(source, LineIndexer);

            lastFoundOffset = -1;
            View.SetProvider(provider);

            // The line count (and so the scrollbar extent) grows as the background indexer discovers
            // lines; poll for that and stop once indexing completes.
            indexRefreshTimer.Start();

            View.Focus();
            return Task.CompletedTask;
        }

        public void DisposeFile()
        {
            indexRefreshTimer.Stop();
            View.SetProvider(null);
            provider = null;
            source?.Dispose();
            source = null;
        }

        private void IndexRefreshTimer_Tick(object? sender, EventArgs e)
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
            }
        }

        public Task GoToLineNumber(int lineNumber)
        {
            if (provider is null)
            {
                return Task.CompletedTask;
            }

            int target = Math.Max(0, lineNumber - 1);
            if (target < provider.LineCount)
            {
                View.GoToLine(target);
                lastFoundOffset = -1;
            }

            return Task.CompletedTask;
        }

        public Task FindText(string text, Progress<int> progress, CancellationToken token)
        {
            long startOffset = GetSearchStartOffset();
            byte[] pattern = Encoding.UTF8.GetBytes(text);

            return Task.Run(async () =>
            {
                await foreach (var matchOffset in searcher.Search(startOffset, pattern, progress, token))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        int line = LineIndexer.GetLineNumberFromOffset(matchOffset);
                        long lineStart = LineIndexer.GetOffsetFromLineNumber(line + 1);
                        int charColumn = provider!.ByteColumnToCharColumn(line, matchOffset - lineStart);
                        lastFoundOffset = matchOffset;
                        View.SelectMatch(line, charColumn, text.Length);
                        Application.Current.MainWindow?.Activate();
                    });
                    break;
                }
            });
        }

        private long GetSearchStartOffset()
        {
            if (lastFoundOffset >= 0)
            {
                return lastFoundOffset + 1;
            }

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
            View.FirstVisibleLine = (int)Math.Round(VScroll.Value);
        }

        private void HScroll_Scroll(object sender, ScrollEventArgs e)
        {
            if (syncingScroll)
            {
                return;
            }
            View.HorizontalOffset = HScroll.Value;
        }
    }
}
