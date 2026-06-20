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
`<value>`s — a **pt-BR** (Brazilian Portuguese) translation ships as a worked example. Keys must match `en-US`
exactly; any missing key falls back to `en-US`.

To preview a language **without changing your Windows display language**, set the `FUJIY_LANG` environment
variable to a BCP-47 tag before launching:

```powershell
$env:FUJIY_LANG = 'pt-BR'; .\FujiyNotepad.WinUI.exe
```

(`App.ApplyLanguageOverride` reads it and sets `ApplicationLanguages.PrimaryLanguageOverride` before any string
resolves.) Unset it to follow Windows. Numbers stay culture-aware via `:N0` / `CurrentCulture`.

## Status / what is converted

- **Done:** the whole **menu bar**, the **Find/Filter bars** (placeholders, button text, tooltips, and
  `AutomationProperties.Name`s via the attached-property `x:Uid` syntax), the interactive **status-bar links**,
  and **every code-behind string** — window/`<stdin>` titles, status (`Following`, `counting…`), all
  **dialogs** (Go To Line/Offset/Percentage, the open-error dialog, About, Highlight Rules). Verified to resolve
  in **en-US and pt-BR** under Native AOT.
- **Intentionally left literal:** proper nouns / technical identifiers — font family names, encoding names
  (`UTF-8`, …), the tab-width digits, and the highlight-rule colour/flag keywords the parser accepts
  (`red`, `/regex`, …), which the user types verbatim.

## Gotchas

- Code-behind `LocalizedStrings.Get/Format` needs **flat** keys (`InsertPresetText`); the dotted
  `Element.Property` form is only for XAML `x:Uid`. The Windows App SDK `ResourceLoader.GetString` **throws**
  for a missing key, so `LocalizedStrings.Get` catches it and returns the key as a visible fallback.

