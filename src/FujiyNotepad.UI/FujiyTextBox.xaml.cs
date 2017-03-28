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
using System.Windows.Input;

namespace FujiyNotepad.UI
{
    /// <summary>
    /// Interaction logic for FujiyTextBox.xaml
    /// </summary>
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

            GoToOffset(0);
            ContentScrollBar.Value = 0;
            TxtContent.Focus();
        }

        public void GoToLineNumber(int lineNumber)
        {
            if (LineIndexer.GetNumberOfLinesIndexed() > lineNumber)
            {
                long offset = LineIndexer.GetOffsetFromLineNumber(lineNumber);

                UpdateScrollBarFromOffset(offset);

                GoToOffset(offset);
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

        private void UpdateScrollBarFromOffset(long offset)
        {
            double newScrollValue = offset * ContentScrollBar.Maximum / fileSize;
            ContentScrollBar.Value = newScrollValue;
        }

        private void ScrollBar_Scroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
        {
            //TODO implementar Large Increment/Decrement
            //Mas antes juntar com o codigo do PreviewKeyDown
            //A diferença é que no PreviewKeyDown o Caret tem que se mover junto
            if (e.ScrollEventType == ScrollEventType.SmallDecrement)
            {
                ScrollLineUp();
            }
            else if(e.ScrollEventType == ScrollEventType.SmallIncrement)
            {
                ScrollLineDown();
            }
            else
            {
                //TODO evitar que seja chamado varias vezes em menos de 100ms
                long startOffset = (long)(fileSize * e.NewValue / ContentScrollBar.Maximum);

                GoToOffset(startOffset);
            }
        }

        //TODO é possível juntar com o codigo do TxtContent_PreviewKeyDown?
        private void ScrollLineUp()
        {
            int lineIndex = TxtContent.GetLineIndexFromCharacterIndex(TxtContent.CaretIndex);
            int column = TxtContent.SelectionStart - TxtContent.GetCharacterIndexFromLineIndex(lineIndex);

            long startOffset = Math.Max(lastOffset - 1, 0);
            long? nextLineOffset = searcher.SearchBackward(startOffset, '\n', new Progress<int>()).Cast<long?>().FirstOrDefault() + 1;

            if (nextLineOffset != null)
            {
                GoToOffset(nextLineOffset.Value);
                UpdateScrollBarFromOffset(nextLineOffset.Value);

                //int newLineIndex = lineIndex + 1;
                //int newColumn = Math.Min(column, TxtContent.GetLineLength(newLineIndex));
                TxtContent.CaretIndex = TxtContent.GetCharacterIndexFromLineIndex(lineIndex + 1) + column;
            }
        }

        private void ScrollLineDown()
        {
            int lineIndex = TxtContent.GetLineIndexFromCharacterIndex(TxtContent.CaretIndex);
            int column = TxtContent.SelectionStart - TxtContent.GetCharacterIndexFromLineIndex(lineIndex);

            long? nextLineOffset = searcher.Search(lastOffset, '\n', new Progress<int>()).Cast<long?>().FirstOrDefault() + 1;

            if (nextLineOffset != null)
            {
                GoToOffset(nextLineOffset.Value);
                UpdateScrollBarFromOffset(nextLineOffset.Value);

                int newLineIndex = Math.Max(lineIndex - 1, 0);
                int newColumn = Math.Min(column, TxtContent.GetLineLength(newLineIndex));
                TxtContent.CaretIndex = TxtContent.GetCharacterIndexFromLineIndex(newLineIndex) + newColumn;
            }
        }

        private void GoToOffset(long startOffset)
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

                TxtContent.Text = sb.ToString();
            }
        }

        private long GetLengthToFillViewport(long startOffset)
        {
            int linesInVewport = CountVisibleLines();
            var nextLineOffset = searcher.Search(startOffset, '\n', new Progress<int>()).Take(linesInVewport).Cast<long?>().LastOrDefault() + 1;
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
                int visibleLines = CountVisibleLines();

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
            int lineIndex = TxtContent.GetLineIndexFromCharacterIndex(TxtContent.CaretIndex);// TxtContent.GetLineIndexFromCharacterIndex(TxtContent.SelectionStart);
            int line = lineIndex + 1;
            int column = TxtContent.SelectionStart - TxtContent.GetCharacterIndexFromLineIndex(lineIndex);

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
                    linesToScroll = -(CountVisibleLines() - 2);
                    break;
                case Key.PageDown:
                    linesToScroll = CountVisibleLines() - 2;
                    break;
                default:
                    return;
            }

            e.Handled = true;

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
                    nextLineOffset = searcher.Search(lastOffset, '\n', new Progress<int>()).Take(linesToScroll).Cast<long?>().LastOrDefault() + 1;
                }

                if (nextLineOffset != null)
                {
                    GoToOffset(nextLineOffset.Value);
                    UpdateScrollBarFromOffset(nextLineOffset.Value);

                    int newColumn = Math.Min(column, TxtContent.GetLineLength(lineIndex));
                    TxtContent.CaretIndex = TxtContent.GetCharacterIndexFromLineIndex(lineIndex) + newColumn;
                }
            }
        }
    }
}
