using FujiyNotepad.UI.Model;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

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
            //TODO evitar que seja chamado varias vezes em menos de 100ms
            long startOffset = (long)(fileSize * e.NewValue / ContentScrollBar.Maximum);

            GoToOffset(startOffset);
        }

        private void GoToOffset(long startOffset)
        {
            startOffset = Math.Min(startOffset, maximumStartOffset);

            startOffset = searcher.SearchBackward(startOffset, '\n', new Progress<int>()).FirstOrDefault();
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
            var nextLineOffset = searcher.Search(startOffset, '\n', new Progress<int>()).Take(linesInVewport).LastOrDefault();
            if (nextLineOffset != default(long))
            {
                return nextLineOffset - startOffset;
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
                lineIndex = TxtContent.GetLastVisibleLineIndex();
                TxtContent.Text = temp;
                //TODO as vezes retorna 500
            }
            return lineIndex + 1;
        }

        private void TxtContent_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            //int line = TxtContent.GetLineIndexFromCharacterIndex(TxtContent.SelectionStart);
            //int column = TxtContent.SelectionStart - TxtContent.GetFirstCharIndexFromLine(line);

            int linesToScroll;

            switch (e.Key)
            {
                case Key.Up:
                    //TODO só deveria fazer o scroll caso esteja na primeira linha
                    linesToScroll = -1;
                    break;
                case Key.Down:
                    //TODO só deveria fazer o scroll caso esteja na ultima linha
                    linesToScroll = 1;
                    break;
                case Key.PageUp:
                    linesToScroll = -(CountVisibleLines() - 2);
                    break;
                case Key.PageDown:
                    linesToScroll = CountVisibleLines() - 2;
                    break;
                default:
                    return; //Only to make the compiler happy about the linesToScroll variable
            }

            e.Handled = true;

            if (linesToScroll != 0)
            {
                long? nextLineOffset;
                if (linesToScroll < 0)
                {
                    long startOffset = Math.Max(lastOffset - 1, 0);
                    nextLineOffset = searcher.SearchBackward(startOffset, '\n', new Progress<int>()).Take(-linesToScroll).Cast<long?>().LastOrDefault();
                }
                else
                {
                    nextLineOffset = searcher.Search(lastOffset, '\n', new Progress<int>()).Take(linesToScroll).Cast<long?>().LastOrDefault();
                }

                if (nextLineOffset != null)
                {
                    GoToOffset(nextLineOffset.Value);
                    UpdateScrollBarFromOffset(nextLineOffset.Value);
                }
            }
        }
    }
}
