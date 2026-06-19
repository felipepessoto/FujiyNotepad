# Localization (issue #78)

User-facing strings live in resource files so the UI can be translated. English (`en-US`) is the default and
the ultimate fallback.

## Where strings live

- `Strings/en-US/Resources.resw` — the string table. It compiles into the app's `resources.pri` (which already
  ships next to the unpackaged, Native-AOT executable), so both XAML and code-behind resolve strings at runtime.

## How to use it

**XAML** — give an element an `x:Uid` and the framework fills its properties from matching keys:

```xml
<MenuFlyoutItem x:Uid="OpenItem" Text="Open..." Click="Open_Click" />
```

reads `OpenItem.Text` from the `.resw`. The design-time `Text="..."` is kept as a readable fallback; the
resource value wins at runtime. Other properties work the same way: `.Title` (MenuBarItem), `.Content`
(Button / HyperlinkButton), `.PlaceholderText` (TextBox), and attached properties via the bracket syntax,
e.g. `MyButton.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip`.

**Code-behind** — read keys through the `LocalizedStrings` helper (an AOT-safe wrapper over the Windows App SDK
`ResourceLoader`):

```csharp
Title = LocalizedStrings.Get("AppDisplayName");
Title = LocalizedStrings.Format("WindowTitleWithFile", Path.GetFileName(path)); // "{0} - Fujiy Notepad"
```

`Format` uses `CultureInfo.CurrentCulture`. Numbers already format with `:N0` (culture-aware) in `StatusText`.

## Adding a translation

Copy `Strings/en-US/Resources.resw` to `Strings/<bcp47>/Resources.resw` (e.g. `Strings/pt-BR/`) and translate the
`<value>`s. To smoke-test a language without changing Windows, set
`Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "pt-BR";` early in startup and
confirm the strings resolve from the new table (falling back to `en-US` for any missing key).

## Status / what is converted

- Done: the whole **menu bar** (titles + items) and the interactive **status-bar links**, plus the key
  **code-behind** strings (window title, `<stdin>` title, the "Following" status, "counting…") — these establish
  every binding pattern the app uses and are verified to resolve under Native AOT.
- Intentionally left literal: proper nouns / technical identifiers (font family names, encoding names like
  `UTF-8`, the tab-width digits).
- Follow-up (same pattern): the Find/Filter bar tooltips and `AutomationProperties.Name`s, and the
  `ContentDialog` bodies built in code (Go To…, About, Highlight Rules, error dialogs). Move each into the
  `.resw` and read it via `x:Uid` (attached-property syntax) or `LocalizedStrings`.
