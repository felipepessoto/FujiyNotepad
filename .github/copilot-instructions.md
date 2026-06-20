# Copilot instructions for FujiyNotepad

FujiyNotepad is a **read-only viewer for very large text/log files** (multi-GB) on Windows: WinUI 3 +
Windows App SDK, published as a **self-contained Native AOT** app. It opens huge files instantly because it
never loads the whole file — it indexes lazily and decodes only the visible lines. It is a **single-file
viewer** by design (multiple windows / tabs / single-instance were tried and deliberately rejected; don't
reintroduce them).

## Build, test, run

From the repository root (requires the .NET 10 SDK; `global.json` pins `10.0.100` / `latestMinor`):

```powershell
dotnet build src/FujiyNotepad.slnx -c Release
dotnet run --project src/FujiyNotepad.WinUI -c Release          # run the app (managed dev build)
dotnet test src/FujiyNotepad.slnx -c Release -p:Platform=x64    # all engine + presentation tests
```

Run a **single** test project / class / method (the test projects are plain .NET, no device needed):

```powershell
dotnet test src/FujiyNotepad.Presentation.Tests/FujiyNotepad.Presentation.Tests.csproj -c Release
dotnet test src/FujiyNotepad.Core.Tests/FujiyNotepad.Core.Tests.csproj -c Release --filter "FullyQualifiedName~LineIndexerTests"
dotnet test src/FujiyNotepad.Presentation.Tests/FujiyNotepad.Presentation.Tests.csproj -c Release --filter "FullyQualifiedName~SelectionHighlightPolicyTests.Word_OnSingleLine_ReturnsTerm"
```

There is no separate lint step; the build runs with warnings-as-signal — keep it **0 warnings / 0 errors**.

### Native-AOT publish (do this for any UI-affecting change)

```powershell
dotnet publish src/FujiyNotepad.WinUI/FujiyNotepad.WinUI.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64 -o publish/app
```

The app's worst bugs (CsWinRT picker marshalling, missing `resources.pri`) **only reproduce in the published
AOT build**, not in `dotnet run`. Two practical gotchas:

- The ILC link step needs the C++ toolchain and `vswhere.exe`. If publish fails with `'vswhere.exe' is not
  recognized`, prepend `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH` before invoking
  `vcvars64.bat` + `dotnet publish` in the same `cmd` process.
- **After adding/renaming an `x:Name` or `Click` in XAML, delete `src/FujiyNotepad.WinUI/obj` and `bin`
  before publishing.** A stale `MainWindow.g.cs` from a prior publish causes `CS1061 'MainWindow' does not
  contain a definition for …` even though `dotnet build` succeeds.

### UI tests

The WinUI app can't start headlessly, so it's covered by an app-layer UI-Automation sweep over the
**published** exe (needs `winapp` CLI: `winget install Microsoft.winappcli`):

```powershell
./scripts/ui-tests.ps1 -Exe publish/app/FujiyNotepad.WinUI.exe
```

`winapp ui` can invoke menus, read control values/properties, and assert no crash — but it has **no key-send
verb and synthetic mouse input does not reach the Win2D canvas**, so selection/pointer/keyboard-driven
behaviour can't be driven end-to-end here. Unit-test that logic in `Presentation`; the painted visual is
confirmed by a human on a real desktop.

### Benchmarks

`dotnet run -c Release --project src/FujiyNotepad.Benchmarks` (BenchmarkDotNet over indexing/search/line
retrieval). Manual + a non-gating workflow; correctness of the same paths is gated by `LargeFileIntegrationTests`.

## Architecture

Layered so **almost all logic is device-free and unit-tested**; `FujiyNotepad.WinUI` is a thin shell.

| Project | Role |
| --- | --- |
| `FujiyNotepad.Core` | UI-agnostic engine: positional/thread-safe file reads (`FileByteSource`), chunked/vectorized byte search (`TextSearcher`), the **sparse** background line index (`LineIndexer`: 1 checkpoint / 1024 lines + LRU block cache, so ~1 MB even at 100M lines), on-demand line decode (`LineProvider`), encodings. |
| `FujiyNotepad.Presentation` | Framework-independent view logic: scroll/caret/selection, the **per-line render model**, find/filter/highlight/bookmarks, settings, status-bar formatting. **No Win2D/WinUI dependency.** |
| `FujiyNotepad.WinUI` | The app shell: `Controls/TextCanvas.cs` (Win2D surface) + `MainWindow.xaml(.cs)` (menus, scrollbars, status bar, dialogs, pickers). |
| `*.Tests` + `FujiyNotepad.TestSupport` | xUnit tests; shared fakes `InMemoryByteSource` / `GrowableByteSource`. |

**Core rendering dataflow (read these together to understand a render change):** `TextLayoutEngine`
(`Presentation`) holds scroll/caret/selection and emits a `VisibleLine` model per visible row from
`GetVisibleLines()` — pure pixel math, no device. `TextCanvas.OnDraw` (`WinUI`) just issues the matching
`FillRectangle`/`DrawText` calls. So the non-trivial "what to draw" logic stays unit-testable
(`EngineRenderModelTests`). Highlighting uses **separate parallel channels** on the engine, each computed only
for visible lines: `highlighter` (Find), `selectionHighlighter` (occurrences of the selected text),
`highlightRules` (persistent colour rules). When channels overlap, **Find takes precedence** (the
selection-occurrence layer stands down whenever a Find highlight is active). Adding a new highlight layer means
adding a channel + a `VisibleLine` list + a paint pass, mirroring the existing ones.

**When adding logic, put it in `Core` or `Presentation` (device-free, testable) with only thin wiring in
`FujiyNotepad.WinUI`.** The CI coverage gate requires Core + Presentation line coverage ≥ **85%**.

## Conventions

- **Native AOT safety (load-bearing):** no runtime codegen / reflection on hot or serialization paths. Use
  `System.Text.Json` **source generation** for settings (`SettingsJsonContext`), and the **interpreted**
  `Regex` engine (`new Regex(…)`) for user-supplied Find / highlight-rule patterns. Never
  `RegexOptions.Compiled` (emits IL at runtime). `[GeneratedRegex]` is allowed only for patterns fixed at
  compile time (e.g. `TimestampParser`).
- **Settings** persist as JSON at `%LOCALAPPDATA%\FujiyNotepad\settings.json` via `SettingsStore` (in
  `Presentation`, so it's testable). Do **not** use `Windows.Storage.ApplicationData` — it throws in an
  unpackaged app. New setting = add a property to `AppSettings` (give a sensible default) and round-trip it in
  `SettingsStoreTests`.
- **WinUI gotchas:** use `Microsoft.Windows.Storage.Pickers` (the modern picker; the legacy
  `Windows.Storage.Pickers` throws a CsWinRT CCW error under AOT). Custom `AutomationPeer`s must be `partial`
  (CsWinRT1028). A multiline `TextBox` uses a lone `'\r'` as its line separator and truncates a multi-line
  value if `Text` is assigned while `AcceptsReturn` is still `false` — set `AcceptsReturn=true` first, and
  normalize on both `'\r'` and `'\n'` when parsing its text.
- **Localization:** menu/dialog strings live in `src/FujiyNotepad.WinUI/Strings/<lang>/Resources.resw` keyed by
  `x:Uid`. en-US and pt-BR must keep **key parity**; add a key to both.
- **C# style:** block-scoped namespaces; **non-underscore** private field names in app/engine code (e.g.
  `provider`, `currentEncoding`) — do not churn to `_camelCase`; PascalCase types/methods/properties; `Async`
  suffix on async methods; set `AutomationProperties.AutomationId`/`Name` on interactive controls.

## Pull requests

- Branch off an up-to-date `master`; keep PRs focused. Both CI jobs must be green: **Build & Test** (build +
  tests + 85% coverage gate) and **UI tests** (AOT publish + UI Automation).
- Merge with **Squash and merge** (not a merge commit or rebase).
- When cutting a release, update the GitHub Release notes with user-friendly notes (not just the
  auto-generated commit list); add the change under `CHANGELOG.md` `[Unreleased]` in each PR.
- Crash diagnostics for unexpected closes: `%LOCALAPPDATA%\FujiyNotepad\crash.log`.
