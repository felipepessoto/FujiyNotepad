using Microsoft.Win32;
using System;
using System.Collections.Generic;
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
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        CancellationTokenSource cancelIndexingTokenSource;
        public MainWindow()
        {
            InitializeComponent();
        }

        private void MenuGoToLine_Click(object sender, RoutedEventArgs e)
        {
            TextControl.GoToLineNumber(100);//TODO implementar janela para entrar com valor
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();

            bool? result = dlg.ShowDialog();

            if (result == true)
            {
                string filePath = dlg.FileName;
                OpenFile(filePath);
            }
        }

        private void OpenFile(string filePath)
        {
            cancelIndexingTokenSource?.Cancel();
            TextControl.OpenFile(filePath);
            StartOrResumeIndexing();
        }

        private void OpenSample_Click(object sender, RoutedEventArgs e)
        {
            string filePath = @"Sample.txt";
            CreateFakeFile(filePath);
            OpenFile(filePath);
        }

        private void CreateFakeFile(string filePath)
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

        private void StartIndexLineNumber_Click(object sender, RoutedEventArgs e)
        {
            StartOrResumeIndexing();
        }

        private void StopIndexLineNumber_Click(object sender, RoutedEventArgs e)
        {
            cancelIndexingTokenSource.Cancel();
        }

        private async Task StartOrResumeIndexing()
        {
            var progress = new Progress<int>(percent =>
            {
                Title = percent + "% indexed";//TODO criar status bar
            });

            cancelIndexingTokenSource = new CancellationTokenSource();
            await Task.Run(() => { TextControl.LineIndexer.StartTaskToIndexLines(cancelIndexingTokenSource.Token, progress); }, cancelIndexingTokenSource.Token);

        }
    }
}
