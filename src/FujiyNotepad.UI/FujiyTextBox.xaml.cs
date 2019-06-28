using FujiyNotepad.UI.Model;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace FujiyNotepad.UI
{
    public partial class FujiyTextBox : UserControl
    {
        MemoryMappedFile mFile;
        long fileSize;
        string filePath;
        StringBuilder sb = new StringBuilder();
        long maximumStartOffset;
        TextSearcher searcher;
        public LineIndexer LineIndexer { get; set; }
        long lastOffset;

        private bool IsChangingText { get; set; }
        private long CarretSelectionOffset { get; set; }
        private int CarretSelectionLength { get; set; }//TODO implement

        public FujiyTextBox()
        {
            InitializeComponent();
            TxtContent.Margin = new Thickness(0, 0, ContentScrollBar.Width, 0);
            IsEnabled = false;
        }

        public void OpenFile(string filePath)
        {
            IsEnabled = true;
            if (mFile != null)
            {
                mFile.Dispose();//TODO pensar em uma forma melhor de clean, talvez remover todo o User Control
            }

            this.filePath = filePath;
            fileSize = new FileInfo(filePath).Length;

            maximumStartOffset = GetMaximumStartOffset();//TODO precisa atualizar sempre que fizer resize

            mFile = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);

            searcher = new TextSearcher(mFile, fileSize);
            LineIndexer = new LineIndexer(searcher);

            GoToOffset(0, true);
            TxtContent.Focus();
        }

        public void GoToLineNumber(int lineNumber)
        {
            if (LineIndexer.GetNumberOfLinesIndexed() > lineNumber)
            {
                long offset = LineIndexer.GetOffsetFromLineNumber(lineNumber);
                GoToOffset(offset, true);
            }
            else
            {
                if (LineIndexer.IsCompleted)
                {
                    //TODO mensagem
                }
                else
                {
                    //TODO mensagem
                }

            }
        }

        public void FindText(string text)
        {
            var offsetOccurence = searcher.Search(0, text.ToCharArray(), new Progress<int>()).Cast<long?>().FirstOrDefault();
            if (offsetOccurence.HasValue)
            {
                GoToOffset(offsetOccurence.GetValueOrDefault(), true);
            }            
        }

        private void UpdateScrollBarFromOffset(long offset)
        {
            double newScrollValue = offset * ContentScrollBar.Maximum / fileSize;
            ContentScrollBar.Value = newScrollValue;
        }

        private void ScrollBar_Scroll(object sender, ScrollEventArgs e)
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
                    GoToOffset(startOffset, false);
                    return;
            }

            ScrollContent(linesToScroll, true);
        }

        private long GoToOffset(long startOffset, bool updateScrollBar)
        {
            startOffset = Math.Min(startOffset, maximumStartOffset);

            startOffset = searcher.SearchBackward(startOffset, '\n', new Progress<int>()).FirstOrDefault() + 1;
            lastOffset = startOffset;
            long length = GetLengthToFillViewport(startOffset);

            Debug.Assert(length > 0);

            using (var stream = mFile.CreateViewStream(startOffset, length, MemoryMappedFileAccess.Read))
            using (var streamReader = new StreamReader(stream))
            {
                sb.Clear();
                int linesInVewport = CountVisibleLines();
                for (int i = 0; i < linesInVewport && streamReader.EndOfStream == false; i++)
                {
                    if (i == linesInVewport - 1)
                    {
                        sb.Append(streamReader.ReadLine());
                    }
                    else
                    {
                        sb.AppendLine(streamReader.ReadLine());
                    }
                }
                IsChangingText = true;
                TxtContent.Text = sb.ToString();
                UpdateCarretSelection();
                IsChangingText = false;
            }

            if (updateScrollBar)
            {
                UpdateScrollBarFromOffset(startOffset);
            }
            return startOffset;
        }

        private long GetLengthToFillViewport(long startOffset)
        {
            int linesInVewport = CountVisibleLines();
            var nextLineOffset = searcher.Search(startOffset, LineIndexer.LineBreakChar, new Progress<int>()).Take(linesInVewport).Cast<long?>().LastOrDefault() + 1;
            if (nextLineOffset != null)
            {
                return nextLineOffset.Value - startOffset;
            }
            return fileSize - startOffset;
        }

        private long GetMaximumStartOffset()
        {
            using (var fs = File.OpenRead(filePath))
            {
                fs.Seek(0, SeekOrigin.End);
                int newLines = 0;
                int visibleLines = CountVisibleLines() - 1;//TODO CountVisibleLines() - 2;//Rows behind horizontal scrollbar

                while (newLines < visibleLines && fs.Position > 0)//TODO testar arquivo pequeno
                {
                    fs.Seek(-2, SeekOrigin.Current);
                    newLines += fs.ReadByte() == '\n' ? 1 : 0;
                }

                return Math.Min(fs.Position, fileSize - 1);
            }
        }

        private int CountVisibleLines()
        {
            //TODO teste
            int lines = (int)Math.Floor((TxtContent.VerticalOffset + TxtContent.ViewportHeight - 1) / (TxtContent.FontFamily.LineSpacing * TxtContent.FontSize));
            return lines;

            //Implement cache to the result. Refresh on resize.
            int lineIndex;

            if (TxtContent.Text != string.Empty)
            {
                lineIndex = TxtContent.GetLastVisibleLineIndex();
            }
            else
            {
                //TODO tratar casos onde tem texto mas não preenche a tela toda
                string temp = TxtContent.Text;
                TxtContent.Text = new string('\n', 500);
                TxtContent.GetLineIndexFromCharacterIndex(0);//Workaround to GetLastVisibleLineIndex work at first
                lineIndex = TxtContent.GetLastVisibleLineIndex();
                TxtContent.Text = temp;
            }
            return lineIndex + 1;
        }

        private void TxtContent_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            int lineIndex = GetCarretLineIndex();
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
                        GoToOffset(0, true);
                    }
                    return;
                case Key.End:
                    if (e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        GoToOffset(maximumStartOffset, true);
                    }
                    return;
                default:
                    return;
            }

            ScrollContent(linesToScroll, false);

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

        private void ScrollContent(int linesToScroll, bool keepCaretAtSameLine)
        {
            if (linesToScroll != 0)
            {
                long? nextLineOffset;
                if (linesToScroll < 0)
                {
                    long startOffset = Math.Max(lastOffset - 1, 0);
                    nextLineOffset = searcher.SearchBackward(startOffset, '\n', new Progress<int>()).Take(-linesToScroll).Cast<long?>().LastOrDefault() + 1;
                }
                else
                {
                    nextLineOffset = searcher.Search(lastOffset, LineIndexer.LineBreakChar, new Progress<int>()).Take(linesToScroll).Cast<long?>().LastOrDefault() + 1;
                }

                if (nextLineOffset != null)
                {
                    GoToOffset(nextLineOffset.Value, true);
                }
            }
        }

        private void UpdateCarretSelection()
        {
            var lastSelectedCharOffset = CarretSelectionOffset + CarretSelectionLength;

            if (lastSelectedCharOffset < lastOffset || CarretSelectionOffset > (lastOffset + TxtContent.Text.Length))
            {
                TxtContent.IsReadOnlyCaretVisible = false;
            }
            else
            {
                TxtContent.IsReadOnlyCaretVisible = true;
                TxtContent.SelectionStart = Math.Max((int)(CarretSelectionOffset - lastOffset), 0);

                int countSelectedCharsNotVisible = Math.Max((int)(lastOffset - CarretSelectionOffset), 0);
                TxtContent.SelectionLength = CarretSelectionLength - countSelectedCharsNotVisible;
            }
        }

        private int GetCarretLineIndex()
        {
            return TxtContent.GetLineIndexFromCharacterIndex(TxtContent.CaretIndex);
        }

        private void TxtContent_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (IsChangingText == false)
            {
                TxtContent.IsReadOnlyCaretVisible = true;
                var txtContent = ((TextBox)e.OriginalSource);
                //TODO if the user changes the end selection while the selected text is only partially visible, it inadvertently recalculates the CarretSelectionOffset
                CarretSelectionOffset = txtContent.SelectionStart + lastOffset;
                CarretSelectionLength = txtContent.SelectionLength;
            }
        }
    }
}
