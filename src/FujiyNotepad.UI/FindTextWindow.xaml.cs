using System.Windows;

namespace FujiyNotepad.UI
{
    /// <summary>
    /// Interaction logic for FindTextWindow.xaml
    /// </summary>
    public partial class FindTextWindow : Window
    {
        public string TextToFind { get; private set; }

        public FindTextWindow()
        {
            InitializeComponent();
            TxtTextToFind.Focus();
        }

        private void BtnFind_Click(object sender, RoutedEventArgs e)
        {
            TextToFind = TxtTextToFind.Text;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
