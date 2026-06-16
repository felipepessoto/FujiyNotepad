# FujiyNotepad

[![CI](https://github.com/felipepessoto/FujiyNotepad/actions/workflows/ci.yml/badge.svg)](https://github.com/felipepessoto/FujiyNotepad/actions/workflows/ci.yml)

A lightweight Windows text viewer (**WinUI 3**) built to open and navigate **very large text
files** that would be slow or impossible to load in a conventional editor.

Instead of reading the whole file into memory, FujiyNotepad reads it on demand in chunks (via
the positional `RandomAccess` API) and draws only the lines currently on screen with a **custom
text view** built from scratch on [Win2D](https://github.com/microsoft/Win2D) (`CanvasControl` +
DirectWrite), not a `TextBox`. Scrolling, paging, *Go To Line*, and *Find* stream directly from
disk using a vectorized byte search (`ReadOnlySpan<byte>.IndexOf`), so memory usage stays roughly
constant regardless of file size.

> **Editions:** FujiyNotepad started as a WPF app and was migrated to WinUI 3. The WinUI edition is
> now the sole, go-forward app; the WPF edition has been retired. Releases are tagged `v*`.

## Download

Grab the latest build from the [Releases page](https://github.com/felipepessoto/FujiyNotepad/releases).
Each release publishes a **self-contained** [Native AOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
build for every Windows architecture — each bundles the .NET runtime **and** the Windows App SDK runtime,
so you just unzip and run `FujiyNotepad.WinUI.exe` with **no prerequisites**, nothing to install (~22 MB):

| Your PC | Download |
| --- | --- |
| Most desktops & laptops (Intel/AMD, 64-bit) | **`FujiyNotepad-<version>-win-x64.zip`** |
| Windows on Arm (e.g. Snapdragon, Surface Pro X) | **`FujiyNotepad-<version>-win-arm64.zip`** |
| 32-bit Windows | **`FujiyNotepad-<version>-win-x86.zip`** |

The builds are unsigned, so Windows SmartScreen may warn on first run (*More info → Run anyway*).

## Features

- Open multi-gigabyte text files with near-constant memory usage — a custom view draws only the
  lines currently on screen, reading each on demand.
- **Open a file** by dragging it onto the window, from *File ▸ Open* (`Ctrl+O`), or via a command-line
  argument / file association; recently opened files appear under *File ▸ Open Recent*.
- **Tabs** — open several files at once in one window, each in its own tab (`Ctrl+T` for a new tab,
  `Ctrl+W` to close one), so logs can be compared and cross-referenced.
- **Reload** the open file (`F5`, or *File ▸ Reload*) to pick up on-disk changes, keeping the scroll
  position; when the file changes underneath you, a non-blocking **"file changed on disk"** hint
  appears in the status bar so you can reload on demand (handy for growing logs).
- **Looks at home on Windows**: a Mica backdrop on Windows 11, the open file's name in the title bar,
  and a text view that follows the system **light / dark** theme.
- Native-style navigation: mouse wheel, a line-based vertical scrollbar, a horizontal scrollbar,
  and the keyboard (arrows, `PageUp`/`PageDown`, `Home`/`End`, `Ctrl+Home`/`Ctrl+End`).
- Character-level **selection** (mouse drag with continuous edge auto-scroll, `Shift`+click/arrows,
  double-click to select a word) and **copy** (`Ctrl+C`), with a caret and an `Ln, Col` indicator.
- Background **line indexing** with progress reporting and cancellation; the scrollbar extent grows
  as the indexer discovers lines.
- **Go To Line** (`Ctrl+G`) and **Go To Offset** (`Ctrl+Shift+G`, decimal or `0x` hex byte position), plus
  a non-modal **Find** bar (`Ctrl+F`): find next (`Enter`/`F3`) and
  find previous (`Shift+Enter`/`Shift+F3`), **match case**, **whole word**, **regular expressions**,
  a live **match count**, and **all matches highlighted** in the viewport, with a progress bar and cancel.
- **Character encoding**: auto-detects **UTF-8**, **UTF-16** (LE/BE) and **UTF-32** (LE/BE) from the
  byte-order mark or a heuristic, with **Windows-1252 (ANSI)** as a fallback, so Windows logs and
  exports don't render as mojibake. The *Encoding* menu shows the detection and lets you override it.
- Configurable **tab width** (2 / 4 / 8) and **wide / CJK glyph** handling (East Asian wide and
  fullwidth characters occupy two columns).
- **Zoom** (`Ctrl`+`+` / `Ctrl`+`-` / `Ctrl`+`0`, or `Ctrl`+mouse wheel) with the current percentage in
  the status bar, an optional **line-number gutter** (*View ▸ Line Numbers*), a choice of **monospace
  fonts** (*View ▸ Font*), and the file's total **line and character counts** shown in the status bar
  (alongside the encoding, the **line-ending** type — LF / CRLF / Mixed — and the `Ln, Col` caret position).
- *File ▸ Open Sample* generates a large (~10M line) file — with a short feature-demo header — to
  showcase performance.

## Requirements

- Windows 10 (version 1809 / build 17763) or Windows 11 — **x64, Arm64, or x86**.
- Released builds need **nothing** installed (self-contained Native AOT).
- To build from source: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Running an
  unpackaged **dev build** also needs the [Windows App SDK runtime](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)
  (already on most dev machines); released self-contained builds bundle it, so they need nothing.

## Build & run

```powershell
# From the repository root
dotnet build src/FujiyNotepad.WinUI.slnx -c Release
dotnet run --project src/FujiyNotepad.WinUI -c Release
```

Or open `src/FujiyNotepad.WinUI.slnx` in Visual Studio 2022 (17.13+, for the `.slnx` solution format) and press F5.

The app is **unpackaged** (`WindowsPackageType=None`), so the produced `.exe` runs directly. The
release build additionally uses **Native AOT** (`PublishAot=true`); building AOT locally requires
the *Desktop development with C++* workload (the CI runner already has it).

## Release

A `v*` tag triggers [`release.yml`](.github/workflows/release.yml), which publishes a self-contained
**Native AOT** build for each architecture (`win-x64`, `win-arm64`, `win-x86`) — native code with the
.NET and Windows App SDK runtimes bundled — then zips it and attaches it to a GitHub Release. The same
workflow has a manual `workflow_dispatch` dry-run that produces the artifacts without creating a release.

## Project layout

| Path | Description |
| --- | --- |
| `src/FujiyNotepad.Core` | UI-agnostic engine: `FileByteSource` (positional `RandomAccess` I/O), `TextSearcher` (chunked, vectorized byte search), `LineIndexer` (background line-offset index), `LineProvider` (decode a line on demand), `LineColumns` (tab/column + wide-glyph mapping). |
| `src/FujiyNotepad.Core.Tests` | xUnit tests for the engine (run headless, no UI). |
| `src/FujiyNotepad.WinUI.Logic` | Framework-independent text-view logic: scroll/caret/selection, hit-testing, word selection, copy, and the per-line render model. No Win2D/WinUI dependency, so it unit-tests on a normal test host. |
| `src/FujiyNotepad.WinUI.Logic.Tests` | xUnit tests for the view logic and render model. |
| `src/FujiyNotepad.WinUI` | The WinUI 3 app: `Controls/TextCanvas.cs` (Win2D surface that paints the engine's render model and forwards input) and `MainWindow` (menus, scrollbars, status bar, Go To Line, Find bar). |

## Architecture

WinUI has no text control that virtualizes over a file — its virtualization seam lives only in the
items controls — so the view is a **custom text surface drawn from scratch with Win2D**
(`CanvasControl` + DirectWrite), not a `TextBox`/`ListView`. `TextCanvas` is a thin Win2D shell: it
owns the `CanvasControl`, font metrics, focus, the clipboard, and the caret-blink/auto-scroll timers,
maps pointer/keyboard input to the `TextLayoutEngine`, and paints its per-line render model. All scroll/caret/
selection math lives in `FujiyNotepad.WinUI.Logic`, and the on-disk engine lives in
`FujiyNotepad.Core`; both are free of any Win2D/WinUI/WinRT dependency, so they're covered by xUnit
tests that run on a normal .NET host. Windows App SDK UI can't be driven on headless CI, so
interaction and visual details (selection feel, scrolling, rendering crispness) are best confirmed
hands-on on a real desktop.

## License

See [LICENSE](LICENSE).
