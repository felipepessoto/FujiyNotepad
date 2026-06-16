using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujiyNotepad.WinUI.Logic;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace FujiyNotepad.WinUI
{
    /// <summary>
    /// The application window: a shell that hosts one or more open files as tabs in a <see cref="TabView"/>.
    /// Each tab is a self-contained <see cref="DocumentView"/> (its own menu, find bar, canvas, status and
    /// per-file state), so switching tabs natively preserves each file's scroll / find state. The shell owns
    /// the window chrome (icon, Mica backdrop, size persistence) and the app/tab lifecycle.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly SettingsStore settingsStore = SettingsStore.Default();
        private readonly nint windowHandle;
        private SizeInt32 lastNormalSize;

        public MainWindow()
        {
            this.InitializeComponent();

            windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);

            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "FujiyNotepad.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }

            RestoreWindowSize();

            Closed += (_, _) =>
            {
                SaveWindowState();
                foreach (DocumentView doc in AllDocuments())
                {
                    doc.Cleanup();
                }
            };

            // Open a file passed on the command line (file association / "open with" / drag-onto-exe) in the
            // first tab; otherwise start with a single empty tab.
            string? fileArg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
            DocumentView first = AddDocument();
            if (fileArg != null)
            {
                first.OpenPath(fileArg);
            }
        }

        private IEnumerable<DocumentView> AllDocuments() =>
            Tabs.TabItems.OfType<TabViewItem>().Select(t => t.Content).OfType<DocumentView>();

        private DocumentView? ActiveDocument =>
            (Tabs.SelectedItem as TabViewItem)?.Content as DocumentView;

        // Creates a new empty document tab, wires its title / exit events, selects it, and returns it.
        private DocumentView AddDocument()
        {
            var doc = new DocumentView { WindowHandle = windowHandle };
            var tab = new TabViewItem { Header = doc.DocTitle, Content = doc };

            doc.TitleChanged += () =>
            {
                tab.Header = doc.DocTitle;
                if (ReferenceEquals(ActiveDocument, doc))
                {
                    Title = $"{doc.DocTitle} - Fujiy Notepad";
                }
            };
            doc.ExitRequested += Close;

            Tabs.TabItems.Add(tab);
            Tabs.SelectedItem = tab;
            return doc;
        }

        private void CloseTab(TabViewItem tab)
        {
            (tab.Content as DocumentView)?.Cleanup();
            Tabs.TabItems.Remove(tab);
            if (Tabs.TabItems.Count == 0)
            {
                Close(); // closing the last tab closes the window (and exits the app)
            }
        }

        private void Tabs_AddTabButtonClick(TabView sender, object args) => AddDocument();

        private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Tab is TabViewItem tab)
            {
                CloseTab(tab);
            }
        }

        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Title = ActiveDocument is { } doc ? $"{doc.DocTitle} - Fujiy Notepad" : "Fujiy Notepad";
        }

        private void NewTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            AddDocument();
            args.Handled = true;
        }

        private void CloseTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (Tabs.SelectedItem is TabViewItem tab)
            {
                CloseTab(tab);
            }
            args.Handled = true;
        }

        // ----- Window size persistence (read-modify-write so it doesn't clobber the documents' settings) -----

        private void RestoreWindowSize()
        {
            AppSettings s = settingsStore.Load();
            if (s.WindowWidth >= 320 && s.WindowHeight >= 240)
            {
                AppWindow.Resize(new SizeInt32(s.WindowWidth, s.WindowHeight));
            }
            lastNormalSize = AppWindow.Size;

            if (s.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }

            // Track the most recent non-maximized size so we restore to it, not the maximized bounds.
            AppWindow.Changed += (sender, args) =>
            {
                if (args.DidSizeChange &&
                    sender.Presenter is OverlappedPresenter p &&
                    p.State != OverlappedPresenterState.Maximized)
                {
                    lastNormalSize = sender.Size;
                }
            };
        }

        private void SaveWindowState()
        {
            bool maximized = AppWindow.Presenter is OverlappedPresenter p &&
                             p.State == OverlappedPresenterState.Maximized;
            SizeInt32 size = maximized ? lastNormalSize : AppWindow.Size;

            AppSettings s = settingsStore.Load();
            s.WindowMaximized = maximized;
            if (size.Width > 0 && size.Height > 0)
            {
                s.WindowWidth = size.Width;
                s.WindowHeight = size.Height;
            }
            settingsStore.Save(s);
        }
    }
}
