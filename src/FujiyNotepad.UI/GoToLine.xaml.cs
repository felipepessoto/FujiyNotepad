using System;
using System.Collections.Generic;
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
using System.Windows.Shapes;

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
        }

        private void BtnGoTo_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(TxtLineNumber.Text, out int result))
            {
                LineNumber = result;
            }
            this.Close();
        }
    }
}
