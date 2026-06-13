using FujiyNotepad.UI.Model;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FujiyNotepad.UI
{
    public partial class FujiyTextBox : UserControl
    {
        IByteSource? source;
        long fileSize;
        readonly StringBuilder sb = new();
        long maximumStartOffset;
        TextSearcher searcher = null!;
        public LineIndexer LineIndexer { get; private set; } = null!;
        long lastOffset;

        private const int MaxViewportBytes = 8 * 1024 * 1024;

        private bool IsChangingText { get; set; }
        private long CaretSelectionOffset { get; set; }
        private int CaretSelectionLength { get; set; }//TODO implement

        public FujiyTextBox()
        {
            InitializeComponent();
            TxtContent.Margin = new Thickness(0, 0, ContentScrollBar.Width, 0);
            IsEnabled = false;
        }

        public async Task OpenFile(string filePath)
        {
            IsEnabled = true;
            source?.Dispose();

            source = new FileByteSource(filePath);
            fileSize = source.Length;

            searcher = new TextSearcher(source);
            LineIndexer = new LineIndexer(searcher);

            maximumStartOffset = GetMaximumStartOffset();//TODO: recompute whenever the control is resized

            await GoToOffset(0, true);
            TxtContent.Focus();
        }

        public void DisposeFile()
        {
            source?.Dispose();
            source = null;
        }

        public async Task GoToLineNumber(int lineNumber)
        {
            if (LineIndexer.GetNumberOfLinesIndexed() > lineNumber)
            {
                long offset = LineIndexer.GetOffsetFromLineNumber(lineNumber);
                await GoToOffset(offset, true);
            }
            else
            {
                if (LineIndexer.IsCompleted)
                {
                    //TODO: notify the user that the line number is out of range
                }
                else
                {
                    //TODO: notify the user that indexing is still in progress
                }

            }
        }

        public Task FindText(string text, Progress<int> progress, CancellationToken token)
        {
            return Task.Run(async () =>
            {
                await foreach (var offsetOccurence in searcher.Search(CaretSelectionOffset + CaretSelectionLength, Encoding.UTF8.GetBytes(text), progress, token))
                {
                    await await Dispatcher.InvokeAsync(async () =>
                    {
                        long lineBeginningOffset = await GoToOffset(offsetOccurence, true);
                        TxtContent.Select((int)(offsetOccurence - lineBeginningOffset), text.Length);
                        App.Current.MainWindow?.Activate();
                    });
                    break;
                }
            });
        }

        private void UpdateScrollBarFromOffset(long offset)
        {
            double newScrollValue = offset * ContentScrollBar.Maximum / fileSize;
            ContentScrollBar.Value = newScrollValue;
        }

        private async void ScrollBar_Scroll(object sender, ScrollEventArgs e)
        {
            //TODO evitar que seja chamado varias vezes em menos de 100ms

            int linesToScroll;

            switch (e.ScrollEventType)
            {
                case ScrollEventType.SmallDecrement:
                    linesToScroll = -1;
                    break;
                case ScrollEventType.SmallIncrement:
                    linesToScroll = 1;
                    break;
                case ScrollEventType.LargeDecrement:
                    linesToScroll = -(CountVisibleLines() - 1);//TODO -(CountVisibleLines() - 2);
                    break;
                case ScrollEventType.LargeIncrement:
                    linesToScroll = CountVisibleLines() - 1;//TODO CountVisibleLines() - 2;
                    break;
                default:
                    long startOffset = (long)(fileSize * e.NewValue / ContentScrollBar.Maximum);
                    await GoToOffset(startOffset, false);
                    return;
            }

            await ScrollContent(linesToScroll, true);
        }

        private async Task<long> GoToOffset(long startOffset, bool updateScrollBar)
        {
            startOffset = Math.Min(startOffset, maximumStartOffset);

            startOffset = searcher.SearchBackward(startOffset, (byte)'\n').FirstOrDefault() + 1;
            lastOffset = startOffset;
            long length = await GetLengthToFillViewport(startOffset);

            Debug.Assert(length > 0 || fileSize == 0);

            int windowLength = (int)Math.Min(length, MaxViewportBytes);
            byte[] window = new byte[windowLength];
            int bytesRead = source!.ReadFull(startOffset, window);

            using (var stream = new MemoryStream(window, 0, bytesRead))
            using (var streamReader = new StreamReader(stream))
            {
                sb.Clear();
                int linesInViewport = CountVisibleLines();
                for (int i = 0; i < linesInViewport; i++)
                {
                    string? line = streamReader.ReadLine();
                    if (line is null)
                    {
                        break;
                    }

                    if (i == linesInViewport - 1)
                    {
                        sb.Append(line);
                    }
                    else
                    {
                        sb.AppendLine(line);
                    }
                }
                IsChangingText = true;
                TxtContent.Text = sb.ToString();
                UpdateCaretSelection();
                IsChangingText = false;
            }

            if (updateScrollBar)
            {
                UpdateScrollBarFromOffset(startOffset);
            }
            return startOffset;
        }

        private async Task<long> GetLengthToFillViewport(long startOffset)
        {
            int linesInViewport = CountVisibleLines();

            if (linesInViewport > 0)
            {
                await foreach (var offset in searcher.Search(startOffset, LineIndexer.LineBreak))
                {
                    if (--linesInViewport == 0)
                    {
                        long nextLineOffset = offset + 1;
                        return nextLineOffset - startOffset;
                    }
                }
            }

            return fileSize - startOffset;
        }

        private long GetMaximumStartOffset()
        {
            if (fileSize <= 0)
            {
                return 0;
            }

            int linesFromEnd = CountVisibleLines() - 1;//Rows behind horizontal scrollbar
            if (linesFromEnd <= 0)
            {
                return fileSize - 1;
            }

            // The top-of-viewport offset when scrolled fully down: the start of the line that is
            // `linesFromEnd` newlines back from the end of the file. Scan from fileSize - 1 so the
            // file's terminating newline is not itself counted as a line from the end.
            long offsetAfterLastNewlines = searcher.SearchBackward(fileSize - 1, (byte)'\n')
                .Where(offset => offset >= 0)
                .Take(linesFromEnd)
                .DefaultIfEmpty(-1)
                .Last() + 1;

            return Math.Min(offsetAfterLastNewlines, fileSize - 1);
        }

        private int CountVisibleLines()
        {
            int lines = (int)Math.Floor((TxtContent.VerticalOffset + TxtContent.ViewportHeight - 1) / (TxtContent.FontFamily.LineSpacing * TxtContent.FontSize));
            return lines;
        }

        private async void TxtContent_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            int lineIndex = GetCaretLineIndex();
            int column = TxtContent.SelectionStart - TxtContent.GetCharacterIndexFromLineIndex(lineIndex);
            int line = lineIndex + 1;

            int linesToScroll;

            switch (e.Key)
            {
                case Key.Up:
                    if (line > 1)
                    {
                        return;
                    }
                    linesToScroll = -1;
                    break;
                case Key.Down:
                    if (line < CountVisibleLines())
                    {
                        return;
                    }
                    linesToScroll = 1;

                    break;
                case Key.PageUp:
                    linesToScroll = -(CountVisibleLines() - 1);//TODO -(CountVisibleLines() - 2);
                    break;
                case Key.PageDown:
                    linesToScroll = CountVisibleLines() - 1;//TODO CountVisibleLines() - 2;
                    break;
                case Key.Home:
                    if (e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        await GoToOffset(0, true);
                    }
                    return;
                case Key.End:
                    if (e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        await GoToOffset(maximumStartOffset, true);
                    }
                    return;
                default:
                    return;
            }

            await ScrollContent(linesToScroll, false);

            switch (e.Key)
            {
                case Key.PageUp:
                case Key.PageDown:
                    int newColumn = Math.Min(column, TxtContent.GetLineLength(lineIndex));
                    TxtContent.CaretIndex = TxtContent.GetCharacterIndexFromLineIndex(lineIndex) + newColumn;
                    e.Handled = true;
                    break;
            }
        }

        private async Task ScrollContent(int linesToScroll, bool keepCaretAtSameLine)
        {
            if (linesToScroll != 0)
            {
                long? nextLineOffset;
                if (linesToScroll < 0)
                {
                    long startOffset = Math.Max(lastOffset - 1, 0);
                    nextLineOffset = searcher.SearchBackward(startOffset, (byte)'\n').Take(-linesToScroll).Cast<long?>().LastOrDefault() + 1;
                }
                else
                {
                    nextLineOffset = 1;

                    if (linesToScroll > 0)
                    {
                        await foreach (var offset in searcher.Search(lastOffset, LineIndexer.LineBreak))
                        {
                            if (--linesToScroll == 0)
                            {
                                nextLineOffset = offset + 1;
                                break;
                            }
                        }
                    }
                }

                if (nextLineOffset != null)
                {
                    await GoToOffset(nextLineOffset.Value, true);
                }
            }
        }

        private void UpdateCaretSelection()
        {
            var lastSelectedCharOffset = CaretSelectionOffset + CaretSelectionLength;

            if (lastSelectedCharOffset < lastOffset || CaretSelectionOffset > (lastOffset + TxtContent.Text.Length))
            {
                TxtContent.IsReadOnlyCaretVisible = false;
            }
            else
            {
                TxtContent.IsReadOnlyCaretVisible = true;
                TxtContent.SelectionStart = Math.Max((int)(CaretSelectionOffset - lastOffset), 0);

                int countSelectedCharsNotVisible = Math.Max((int)(lastOffset - CaretSelectionOffset), 0);
                TxtContent.SelectionLength = CaretSelectionLength - countSelectedCharsNotVisible;
            }
        }

        private int GetCaretLineIndex()
        {
            return TxtContent.GetLineIndexFromCharacterIndex(TxtContent.CaretIndex);
        }

        private void TxtContent_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (IsChangingText == false)
            {
                TxtContent.IsReadOnlyCaretVisible = true;
                var txtContent = ((TextBox)e.OriginalSource);
                //TODO: if the user changes the end of the selection while the selected text is only partially visible, the CaretSelectionOffset is inadvertently recalculated
                CaretSelectionOffset = txtContent.SelectionStart + lastOffset;
                CaretSelectionLength = txtContent.SelectionLength;
            }
        }
    }
}
