using Microsoft.Win32;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

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

        private async void MenuGoToLine_Click(object sender, RoutedEventArgs e)
        {
            await GoToLine();
        }

        private async void GoToLineCommand_OnExecuted(object sender, object e)
        {
            if (EditMenu.IsEnabled)
            {
                await GoToLine();
            }
        }

        private async Task GoToLine()
        {
            GoToLine goToWindows = new GoToLine();
            goToWindows.ShowDialog();
            if (goToWindows.LineNumber > 0)
            {
                await TextControl.GoToLineNumber(goToWindows.LineNumber);
            }
        }

        private void MenuFind_Click(object sender, RoutedEventArgs e)
        {
            FindText();
        }

        private void FindTextCommand_OnExecuted(object sender, object e)
        {
            if (EditMenu.IsEnabled)
            {
                FindText();
            }
        }

        private void FindText()
        {
            FindTextWindow findTextWindow = new FindTextWindow(TextControl);
            findTextWindow.ShowDialog();
        }

        private async void Open_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();

            bool? result = dlg.ShowDialog();

            if (result == true)
            {
                string filePath = dlg.FileName;
                await OpenFile(filePath);
            }
        }

        private async Task OpenFile(string filePath)
        {
            cancelIndexingTokenSource?.Cancel();
            await TextControl.OpenFile(filePath);
            EnableMenu();
            StartOrResumeIndexing();
        }

        private void EnableMenu()
        {
            EditMenu.IsEnabled = true;
        }

        private async void OpenSample_Click(object sender, RoutedEventArgs e)
        {
            string filePath = @"Sample.txt";
            CreateFakeFile(filePath);
            await OpenFile(filePath);
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
            StartIndexLineNumber.IsEnabled = false;
            StopIndexLineNumber.IsEnabled = true;
            try
            {
                var progress = new Progress<int>(percent =>
                {
                    LblStatus.Text = percent + "% indexed";
                });

                cancelIndexingTokenSource = new CancellationTokenSource();
                await Task.Run(() => { TextControl.LineIndexer.StartTaskToIndexLines(cancelIndexingTokenSource.Token, progress); }, cancelIndexingTokenSource.Token);

                StopIndexLineNumber.IsEnabled = false;
            }
            catch (OperationCanceledException)
            {
                StartIndexLineNumber.IsEnabled = true;
                StopIndexLineNumber.IsEnabled = false;
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
