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
Pick the architecture for your PC — **x64** for most Intel/AMD machines, **arm64** for Windows on Arm
(Snapdragon, Surface Pro X), or **x86** for 32-bit Windows — then choose one of two flavors:

**Standalone (recommended)** — `FujiyNotepad-<version>-win-<arch>.zip`

A **self-contained** [Native AOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot/) build
that bundles the .NET runtime **and** the Windows App SDK runtime. Unzip and run
`FujiyNotepad.WinUI.exe` — **no prerequisites**, nothing to install (~50 MB).

**Small** — `FujiyNotepad-<version>-win-<arch>-framework-dependent.zip`

A much smaller download (~11 MB) that bundles nothing. It needs both of these installed first:

- the [**.NET 10 Runtime**](https://dotnet.microsoft.com/download/dotnet/10.0) (the plain *.NET Runtime* is enough), and
- the [**Windows App SDK Runtime**](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) (the "Windows App Runtime" redistributable).

Both flavors are unsigned, so Windows SmartScreen may warn on first run (*More info → Run anyway*).

## Features

- Open multi-gigabyte text files with near-constant memory usage — a custom view draws only the
  lines currently on screen, reading each on demand.
- **Looks at home on Windows**: a Mica backdrop on Windows 11, the open file's name in the title bar,
  and a text view that follows the system **light / dark** theme.
- Native-style navigation: mouse wheel, a line-based vertical scrollbar, a horizontal scrollbar,
  and the keyboard (arrows, `PageUp`/`PageDown`, `Home`/`End`, `Ctrl+Home`/`Ctrl+End`).
- Character-level **selection** (mouse drag with continuous edge auto-scroll, `Shift`+click/arrows,
  double-click to select a word) and **copy** (`Ctrl+C`), with a caret and an `Ln, Col` indicator.
- Background **line indexing** with progress reporting and cancellation; the scrollbar extent grows
  as the indexer discovers lines.
- **Go To Line** (`Ctrl+G`) and a non-modal **Find** bar (`Ctrl+F`) with find-next (`Enter`/`F3`),
  a progress bar, and cancel.
- Configurable **tab width** (2 / 4 / 8) and **wide / CJK glyph** handling (East Asian wide and
  fullwidth characters occupy two columns).
- *File ▸ Open Sample* generates a large (~10M line) file — with a short feature-demo header — to
  showcase performance.

## Requirements

- Windows 10 (version 1809 / build 17763) or Windows 11 — **x64, Arm64, or x86**.
- The **standalone** download needs nothing installed; the **small** (framework-dependent) download needs the .NET 10 Runtime and the Windows App SDK Runtime — see [Download](#download).
- To build from source: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

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

## Project layout

| Path | Description |
| --- | --- |
| `src/FujiyNotepad.Core` | UI-agnostic engine: `FileByteSource` (positional `RandomAccess` I/O), `TextSearcher` (chunked, vectorized byte search), `LineIndexer` (background line-offset index), `LineProvider` (decode a line on demand), `LineColumns` (tab/column + wide-glyph mapping). |
| `src/FujiyNotepad.Core.Tests` | xUnit tests for the engine (run headless, no UI). |
| `src/FujiyNotepad.WinUI.Logic` | Framework-independent text-view logic: scroll/caret/selection, hit-testing, word selection, copy, and the per-line render model. No Win2D/WinUI dependency, so it unit-tests on a normal test host. |
| `src/FujiyNotepad.WinUI.Logic.Tests` | xUnit tests for the view logic and render model. |
| `src/FujiyNotepad.WinUI` | The WinUI 3 app: `Controls/TextCanvas.cs` (Win2D surface that paints the engine's render model and forwards input) and `MainWindow` (menus, scrollbars, status bar, Go To Line, Find bar). |

See [`src/FujiyNotepad.WinUI/README.md`](src/FujiyNotepad.WinUI/README.md) for architecture details.

## License

See [LICENSE](LICENSE).
