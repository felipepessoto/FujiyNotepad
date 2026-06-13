using System.Windows;

namespace FujiyNotepad.UI
{
    /// <summary>
    /// Interaction logic for GoToLine.xaml
    /// </summary>
    public partial class GoToLine : Window
    {
        public int LineNumber { get; private set; }

        public GoToLine()
        {
            InitializeComponent();
            TxtLineNumber.Focus();
        }

        private void BtnGoTo_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(TxtLineNumber.Text, out int result))
            {
                LineNumber = result;
            }
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
