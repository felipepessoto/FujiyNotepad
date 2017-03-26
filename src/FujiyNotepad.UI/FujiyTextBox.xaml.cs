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
        long viewPortSize = 1024 * 1024 * 1024;//TODO precisa ser dinamico
        StringBuilder sb = new StringBuilder();
        long maximumStartOffset;
        TextSearcher searcher;
        public LineIndexer LineIndexer { get; set; }
        long lastOffset;

        public FujiyTextBox()
        {
            InitializeComponent();
            TxtContent.Margin = new Thickness(0, 0, ContentScrollBar.Width, 0);
        }

        public void OpenFile(string filePath)
        {
            if (mFile != null)
            {
                mFile.Dispose();//TODO pensar em uma forma melhor de clean, talvez remover todo o User Control
            }

            this.filePath = filePath;

            maximumStartOffset = GetMaximumStartOffset();

            fileSize = new FileInfo(filePath).Length;
            mFile = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);

            searcher = new TextSearcher(mFile, fileSize);
            LineIndexer = new LineIndexer(searcher);

            GoToOffset(0);
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

            startOffset = searcher.SearchNewLineBefore(startOffset);
            lastOffset = startOffset;
            long length = GetLengthToFillViewport(startOffset);

            using (var stream = mFile.CreateViewStream(startOffset, length, MemoryMappedFileAccess.Read))
            using (var streamReader = new StreamReader(stream))
            {
                sb.Clear();

                for (int i = 0; i < 100 && streamReader.EndOfStream == false; i++)
                {
                    sb.AppendLine(streamReader.ReadLine());
                }

                TxtContent.Text = sb.ToString();
            }
        }

        private long GetLengthToFillViewport(long startOffset)
        {
            return Math.Min(fileSize - startOffset, viewPortSize);
        }

        private long GetMaximumStartOffset()
        {
            using (var fs = File.OpenRead(filePath))
            {
                fs.Seek(0, SeekOrigin.End);
                int newLines = 0;

                while (newLines < 3 && fs.Position > 0)//TODO testar arquivo pequeno
                {
                    fs.Seek(-2, SeekOrigin.Current);
                    newLines += fs.ReadByte() == '\n' ? 1 : 0;
                }

                return fs.Position;
            }
        }

        private void TxtContent_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            //int line = TxtContent.GetLineIndexFromCharacterIndex(TxtContent.SelectionStart);
            //int column = TxtContent.SelectionStart - TxtContent.GetFirstCharIndexFromLine(line);

            int linesToScroll;

            switch (e.Key)
            {
                //case Key.Up:
                //    linesToScroll = -1;
                //    break;
                case Key.Down:
                    linesToScroll = 1;
                    break;
                //case Key.PageUp:
                //    linesToScroll = -TxtContent.GetLastVisibleLineIndex();
                //    break;
                case Key.PageDown:
                    linesToScroll = TxtContent.GetLastVisibleLineIndex() - 1;
                    break;
                default:
                    return; //Only to make the compiler happy about the linesToScroll variable
            }

            e.Handled = true;
            var nextLineOffset = searcher.SearchInFile(lastOffset, '\n', new Progress<int>()).Take(linesToScroll).Last();
            if (nextLineOffset != default(long))
            {
                GoToOffset(nextLineOffset);
                UpdateScrollBarFromOffset(nextLineOffset);
            }
        }
    }
}
