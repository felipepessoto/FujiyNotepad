using System;
using System.Collections.Generic;
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
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        MemoryMappedFile mFile;
        long fileSize;
        string filePath = @"Sample.txt";
        long viewPortSize = 15000;//TODO precisa ser dinamico

        public MainWindow()
        {
            InitializeComponent();

            CreateFakeFile();

            fileSize = new FileInfo(filePath).Length;
            mFile = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);

            conteudo.Margin = new Thickness(0, 0, ScrollBarConteudo.Width, 0);
        }

        private void CreateFakeFile()
        {
            if (File.Exists(filePath) == false)
            {
                using (var sampleFile = new StreamWriter(filePath))
                {
                    Random r = new Random();
                    for (int i = 1; i <= 10_000_000; i++)
                    {
                        sampleFile.Write(i);
                        sampleFile.Write(" - ");
                        sampleFile.WriteLine(new string('x', r.Next(300, 500)));
                    }
                }
            }
        }

        private void ScrollBar_Scroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
        {
            UpdateText(e.NewValue);
        }

        private void UpdateText(double scrollValue)
        {
            long startOffset;
            long length;

            if (scrollValue == ScrollBarConteudo.Maximum)
            {
                startOffset = fileSize - viewPortSize;
                length = viewPortSize;
            }
            else
            {
                startOffset = (long)(fileSize * scrollValue / ScrollBarConteudo.Maximum);
                length = Math.Min(fileSize - startOffset, viewPortSize);
            }

            if (length > 0)
            {
                if (scrollValue >= ScrollBarConteudo.Maximum - 1)
                {
                    length = fileSize - startOffset;
                }
                using (var stream = mFile.CreateViewStream(startOffset, length, MemoryMappedFileAccess.Read))
                using (var streamReader = new StreamReader(stream))
                {
                    conteudo.Text = streamReader.ReadToEnd();
                }
            }
        }
    }
}
