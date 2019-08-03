using System;
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
                await Task.Run(() => { TextControl.FindText(TextToFind, ProgressStatus); });
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
