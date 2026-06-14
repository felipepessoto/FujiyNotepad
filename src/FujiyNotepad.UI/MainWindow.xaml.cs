using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace FujiyNotepad.UI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public static readonly RoutedUICommand FindCommand = new("Find", nameof(FindCommand), typeof(MainWindow),
            new InputGestureCollection { new KeyGesture(Key.F, ModifierKeys.Control) });

        public static readonly RoutedUICommand GoToCommand = new("Go To Line", nameof(GoToCommand), typeof(MainWindow),
            new InputGestureCollection { new KeyGesture(Key.G, ModifierKeys.Control) });

        private CancellationTokenSource? cancelIndexingTokenSource;
        private Task indexingTask = Task.CompletedTask;
        private bool isFileOpen;

        public MainWindow()
        {
            InitializeComponent();
            TextControl.CaretPositionChanged += (line, column) =>
                LblCursorPosition.Text = $"Ln {line}, Col {column}";
        }

        private void EditCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = isFileOpen;
        }

        private async void GoToCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            await GoToLine();
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

        private void FindCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            FindText();
        }

        private void FindText()
        {
            FindTextWindow findTextWindow = new FindTextWindow(TextControl);
            findTextWindow.Owner = this;
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
            // Stop and wait for any in-flight indexing before TextControl.OpenFile disposes the
            // current byte source; otherwise the background index could read a disposed source.
            await StopIndexingAsync();
            await TextControl.OpenFile(filePath);
            EnableMenu();
            StartOrResumeIndexing();
        }

        private void EnableMenu()
        {
            isFileOpen = true;
            EditMenu.IsEnabled = true;
            CommandManager.InvalidateRequerySuggested();
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
            cancelIndexingTokenSource?.Cancel();
        }

        private void StartOrResumeIndexing()
        {
            StartIndexLineNumber.IsEnabled = false;
            StopIndexLineNumber.IsEnabled = true;

            cancelIndexingTokenSource = new CancellationTokenSource();
            indexingTask = RunIndexingAsync(cancelIndexingTokenSource.Token);
        }

        private async Task RunIndexingAsync(CancellationToken token)
        {
            try
            {
                var progress = new Progress<int>(percent =>
                {
                    LblStatus.Text = percent + "% indexed";
                });

                await Task.Run(() => TextControl.LineIndexer.StartTaskToIndexLines(token, progress), token);

                StopIndexLineNumber.IsEnabled = false;
            }
            catch (OperationCanceledException)
            {
                StartIndexLineNumber.IsEnabled = true;
                StopIndexLineNumber.IsEnabled = false;
            }
        }

        private async Task StopIndexingAsync()
        {
            cancelIndexingTokenSource?.Cancel();
            try
            {
                await indexingTask;
            }
            catch
            {
                // The previous file's indexing is being torn down; any failure from it is irrelevant
                // to the file about to be opened, so it must not block the switch.
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            cancelIndexingTokenSource?.Cancel();
            TextControl.DisposeFile();
        }
    }
}
