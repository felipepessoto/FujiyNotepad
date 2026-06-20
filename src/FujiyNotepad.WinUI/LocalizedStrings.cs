using Microsoft.Windows.ApplicationModel.Resources;

namespace FujiyNotepad.WinUI
{
    /// <summary>
    /// Resolves localizable code-behind strings from <c>Strings/&lt;lang&gt;/Resources.resw</c> (issue #78).
    /// Uses the Windows App SDK <see cref="ResourceLoader"/> (MRT Core), which works in this unpackaged
    /// Native-AOT app by reading the <c>resources.pri</c> shipped next to the executable. XAML uses
    /// <c>x:Uid</c> for the same table; this helper covers strings built in code (window title, status,
    /// dialogs). Lookups are cheap and the loader is created once.
    /// </summary>
    internal static class LocalizedStrings
    {
        private static readonly ResourceLoader Loader = new();

        /// <summary>Returns the localized string for <paramref name="key"/>, or the key itself if missing.</summary>
        public static string Get(string key)
        {
            try
            {
                string value = Loader.GetString(key);
                return string.IsNullOrEmpty(value) ? key : value;
            }
            catch
            {
                // ResourceLoader.GetString throws (NamedResource Not Found) for a missing key rather than
                // returning empty; degrade gracefully to the key so a missing string never crashes the UI.
                return key;
            }
        }

        /// <summary>Returns the localized format string for <paramref name="key"/> filled with <paramref name="args"/>.</summary>
        public static string Format(string key, params object[] args) =>
            string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), args);
    }
}
