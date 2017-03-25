using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
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
        SortedDictionary<int, long> lineNumberIndex = new SortedDictionary<int, long>();
        long searchSize = 1024 * 1024;

        public FujiyTextBox()
        {
            InitializeComponent();
        }

        public void OpenFile(string filePath)
        {
            this.filePath = filePath;

            maximumStartOffset = GetMaximumStartOffset();

            fileSize = new FileInfo(filePath).Length;
            mFile = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            conteudo.Margin = new Thickness(0, 0, ScrollBarConteudo.Width, 0);

            UpdateTextFromOffset(0);

            Task.Run((Action)StartTaskToIndexLines);
        }

        private void StartTaskToIndexLines()//Task?
        {
            long lastResult = 0;
            bool shouldContinue = true;
            int line = 0;
            Stopwatch c = Stopwatch.StartNew();

            while (shouldContinue)
            {
                shouldContinue = false;

                foreach (long result in SearchInFile(lastResult, '\n'))
                {
                    shouldContinue = true;

                    lineNumberIndex[++line] = result;

                    if (line % 10000 == 0)
                    {
                        c.Stop();
                        Dispatcher.Invoke(() => { App.Current.MainWindow.Title = line.ToString() + " - " + c.ElapsedMilliseconds + "ms"; });
                        c.Restart();
                    }
                }
            }
        }

        private IEnumerable<long> SearchInFile(long startOffset, char charToSearch)
        {
            do
            {
                long bytesToRead = Math.Min(searchSize, fileSize - startOffset);

                using (var stream = mFile.CreateViewStream(startOffset, bytesToRead, MemoryMappedFileAccess.Read))
                using (var streamReader = new StreamReader(stream))
                {
                    int byteRead;
                    do
                    {
                        byteRead = stream.ReadByte();
                        if (byteRead == charToSearch)
                        {
                            yield return startOffset + stream.Position;
                        }
                    } while (byteRead > -1);
                }
                startOffset += bytesToRead;
            }
            while (startOffset < fileSize);
        }

        private void ScrollBar_Scroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
        {
            UpdateTextFromScrollPosition(e.NewValue);
        }

        private void UpdateTextFromScrollPosition(double scrollValue)
        {
            long startOffset = (long)(fileSize * scrollValue / ScrollBarConteudo.Maximum);

            UpdateTextFromOffset(startOffset);
        }

        private void UpdateTextFromOffset(long startOffset)
        {
            startOffset = Math.Min(startOffset, maximumStartOffset);

            startOffset = SearchNewLineBefore(startOffset);

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

        private long SearchNewLineBefore(long startOffset)
        {
            if (startOffset == 0)
            {
                return 0;
            }
            long searchBackOffset = startOffset;
            long searchSizePerIteration = Math.Min(searchSize, startOffset);

            do
            {
                searchBackOffset = Math.Max(searchBackOffset - searchSizePerIteration, 0);

                using (var stream = mFile.CreateViewStream(searchBackOffset, searchSizePerIteration, MemoryMappedFileAccess.Read))
                using (var streamReader = new StreamReader(stream))
                {
                    var newLineAt = streamReader.ReadToEnd().LastIndexOf('\n');

                    if (newLineAt > -1)
                    {
                        return searchBackOffset + newLineAt + 1;
                    }


                    /*
                    stream.Seek(0, SeekOrigin.End);

                    while (stream.Position > 0)
                    {
                        stream.Seek(-1, SeekOrigin.Current);
                        if(stream.ReadByte() == '\n')
                        {
                            return currentPosition;
                        }
                        stream.Seek(-1, SeekOrigin.Current);
                    }
                    */
                }
            }
            while (searchBackOffset > 0);

            return 0;
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
