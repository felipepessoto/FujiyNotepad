using Microsoft.UI.Xaml;

namespace FujiyNotepad.WinUI
{
    public partial class App : Application
    {
        private Window? window;

        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            window = new MainWindow();
            window.Activate();
        }
    }
}
