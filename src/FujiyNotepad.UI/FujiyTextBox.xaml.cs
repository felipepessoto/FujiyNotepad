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
        //SortedDictionary<int, long> lineNumberIndex = new SortedDictionary<int, long>();
        //SortedList<int, long> lineNumberIndex = new SortedList<int, long>();
        List<long> lineNumberIndex = new List<long>();
        TextSearcher searcher;



        public FujiyTextBox()
        {
            InitializeComponent();
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
            conteudo.Margin = new Thickness(0, 0, ScrollBarConteudo.Width, 0);

            searcher = new TextSearcher(mFile, fileSize);

            GoToOffset(0);
        }

        public void GoToLineNumber(int lineNumber)
        {
            if (lineNumberIndex.Count > lineNumber)
            {
                long offset = lineNumberIndex[lineNumber];

                UpdateScrollBarFromOffset(offset);

                GoToOffset(offset);
            }
            else
            {
                //TODO mensagem
            }
        }

        private void UpdateScrollBarFromOffset(long offset)
        {
            double newScrollValue = offset * ScrollBarConteudo.Maximum / fileSize;
            ScrollBarConteudo.Value = newScrollValue;
        }

        public void StartTaskToIndexLines(CancellationToken cancelToken)
        {
            long startOffset = 0;

            if (lineNumberIndex.Count == 0)
            {
                lineNumberIndex.Add(0);
                lineNumberIndex.Add(0);
            }
            else
            {
                startOffset = lineNumberIndex[lineNumberIndex.Count - 1];
            }

            Stopwatch batchTime = Stopwatch.StartNew();
            Stopwatch totalTimeToIndex = Stopwatch.StartNew();

            foreach (long result in searcher.SearchInFile(startOffset, '\n'))
            {
                cancelToken.ThrowIfCancellationRequested();
                lineNumberIndex.Add(result);

                if (lineNumberIndex.Count % 10000 == 0)
                {
                    batchTime.Stop();
                    Dispatcher.Invoke(() => { App.Current.MainWindow.Title = $"Indexed {lineNumberIndex.Count} lines - Last batch took: {batchTime.ElapsedMilliseconds}ms"; });
                    batchTime.Restart();
                }
            }


            Dispatcher.Invoke(() => { App.Current.MainWindow.Title = $"Finished indexing lines. Time: {totalTimeToIndex.ElapsedMilliseconds}ms"; });
        }

        private void ScrollBar_Scroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
        {
            //TODO evitar que seja chamado varias vezes em menos de 100ms
            UpdateTextFromScrollPosition(e.NewValue);
        }

        private void UpdateTextFromScrollPosition(double scrollValue)
        {
            long startOffset = (long)(fileSize * scrollValue / ScrollBarConteudo.Maximum);

            GoToOffset(startOffset);
        }

        private void GoToOffset(long startOffset)
        {
            startOffset = Math.Min(startOffset, maximumStartOffset);

            startOffset = searcher.SearchNewLineBefore(startOffset);

            long length = GetLengthToFillViewport(startOffset);

            using (var stream = mFile.CreateViewStream(startOffset, length, MemoryMappedFileAccess.Read))
            using (var streamReader = new StreamReader(stream))
            {
                sb.Clear();

                for (int i = 0; i < 100 && streamReader.EndOfStream == false; i++)
                {
                    sb.AppendLine(streamReader.ReadLine());
                }

                conteudo.Text = sb.ToString();
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
    }
}
