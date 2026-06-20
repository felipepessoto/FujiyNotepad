using FujiyNotepad.Presentation;
using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FujiyNotepad.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            ApplyLanguageOverride();
            InitializeComponent();

            // Turn an otherwise-silent Native AOT crash into an actionable log under
            // %LOCALAPPDATA%\FujiyNotepad\crash.log. We log but do not mark the exception handled: swallowing
            // an unhandled exception could leave the app in a corrupt state, so we let it terminate as usual.
            // Cover both the UI-thread (WinUI) and any-thread (AppDomain) cases.
            UnhandledException += OnXamlUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        }

        // Lets a user preview a translation (issue #78) without changing their Windows display language: set the
        // FUJIY_LANG environment variable to a BCP-47 tag (e.g. "pt-BR") before launching. Must run before any
        // resource/x:Uid is resolved, so it is the very first thing the app does. Empty/unset = follow Windows.
        private static void ApplyLanguageOverride()
        {
            try
            {
                string? lang = Environment.GetEnvironmentVariable("FUJIY_LANG");
                if (!string.IsNullOrWhiteSpace(lang))
                {
                    Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = lang;
                }
            }
            catch
            {
                // An invalid tag (or the API being unavailable) just means we fall back to the default language.
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
        }

        private void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // The WinUI args can surface a null Exception (a failed cross-ABI marshal) while still carrying a
            // message, so fall back to the message in that case.
            if (e.Exception is { } ex)
            {
                SafeLog(logger => logger.Log(ex));
            }
            else
            {
                SafeLog(logger => logger.Write("UnhandledException", e.Message, null));
            }
        }

        private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                SafeLog(logger => logger.Log(ex));
            }
            else
            {
                SafeLog(logger => logger.Write("UnhandledException", e.ExceptionObject?.ToString(), null));
            }
        }

        // The crash handler must never throw (a second failure would mask the original crash), so resolving
        // the default logger and writing are both wrapped here.
        private static void SafeLog(Action<CrashLogger> log)
        {
            try
            {
                log(CrashLogger.Default());
            }
            catch
            {
                // Intentionally swallowed.
            }
        }
    }
}

