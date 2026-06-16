using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FujiyNotepad.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        // Every open viewer window. WinUI 3 does not terminate the process when the last window closes, so the
        // app tracks its windows and exits explicitly once none remain.
        private static readonly List<Window> _windows = new();

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            MainWindow window = NewWindow();

            // Open a file passed on the command line (file association / "open with" / drag-onto-exe) in the
            // first window. Subsequent windows (New Window / Open in New Window) start empty / load their own.
            string? fileArg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
            if (fileArg != null)
            {
                window.OpenPath(fileArg);
            }

            window.Activate();
        }

        /// <summary>
        /// Creates and registers a new top-level viewer window (caller still <see cref="Window.Activate"/>s it).
        /// The window is removed from the registry on close, and the app exits when the last one closes.
        /// </summary>
        public static MainWindow NewWindow()
        {
            var window = new MainWindow();
            _windows.Add(window);
            window.Closed += (_, _) =>
            {
                _windows.Remove(window);
                if (_windows.Count == 0)
                {
                    Current.Exit();
                }
            };
            return window;
        }
    }
}
