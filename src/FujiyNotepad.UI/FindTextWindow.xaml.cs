using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FujiyNotepad.UI
{
    /// <summary>
    /// Interaction logic for FindTextWindow.xaml
    /// </summary>
    public partial class FindTextWindow : Window
    {
        private string TextToFind { get; set; }
        private FujiyTextBox TextControl { get; }

        Progress<int> ProgressStatus { get; }

        private CancellationTokenSource CancellationTokenSource { get; set; }

        private bool Running { get; set; }

        public FindTextWindow(FujiyTextBox textControl)
        {
            InitializeComponent();
            TxtTextToFind.Focus();
            TextControl = textControl;

            ProgressStatus = new Progress<int>(percent =>
            {
                PgbProgress.Value = percent;
            });
        }

        private async void BtnFind_Click(object sender, RoutedEventArgs e)
        {
            TextToFind = TxtTextToFind.Text;
            if (string.IsNullOrEmpty(TextToFind) == false)
            {
                PgbProgress.Visibility = Visibility.Visible;
                CancellationTokenSource = new CancellationTokenSource();
                Running = true;
                BtnFind.IsEnabled = false;
                await TextControl.FindText(TextToFind, ProgressStatus, CancellationTokenSource.Token);
                Running = false;
                BtnFind.IsEnabled = true;
                PgbProgress.Visibility = Visibility.Hidden;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (Running)
            {
                CancellationTokenSource.Cancel();
            }
            else
            {
                Close();
            }
        }
    }
}
