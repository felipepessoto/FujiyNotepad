# FujiyNotepad — WinUI 3 edition

A WinUI 3 (Windows App SDK) port of FujiyNotepad's large-file viewer. Like the WPF edition,
it opens **multi-gigabyte text files without loading them into memory** — only the lines
currently on screen are read from disk and drawn.

## Architecture

The UI-agnostic engine lives in **`FujiyNotepad.Core`**, and the text-view logic lives in a
separate, framework-independent **`FujiyNotepad.WinUI.Logic`** library so both can be unit-tested
on a normal .NET test host (Windows App SDK test hosts can't run on headless CI runners):

| Project | Description |
| --- | --- |
| `src/FujiyNotepad.Core` | Engine: `FileByteSource` (positional `RandomAccess` I/O), `TextSearcher` (chunked, vectorized byte search), `LineIndexer` (background line-offset index), `LineProvider` (decode a line on demand), `LineColumns` (tab/column + wide-glyph mapping). No UI dependency. |
| `src/FujiyNotepad.Core.Tests` | xUnit tests for the engine (run headless, no UI). |
| `src/winui/FujiyNotepad.WinUI.Logic` | `TextLayoutEngine` — scroll/caret/selection, hit-testing, word selection, copy, and the per-line render model (`GetVisibleLines`). Plus `FindController` (find-next state) and the `TextPosition`/`NavKey` types. No Win2D/WinUI/WinRT dependency. |
| `src/winui/FujiyNotepad.WinUI.Logic.Tests` | xUnit tests for the view logic and render model. |
| `src/winui/FujiyNotepad.WinUI` | The WinUI 3 app. |

The view is a **custom text surface drawn with [Win2D](https://github.com/microsoft/Win2D)**
(`CanvasControl` + DirectWrite), not a `TextBox`/`ListView`. WinUI has no text control that
virtualizes over a file (its virtualization seam lives only in the items controls), so the
viewer is drawn from scratch:

- `Controls/TextCanvas.cs` — a thin Win2D shell: it owns the `CanvasControl`, the font metrics,
  focus, the clipboard and the caret-blink/auto-scroll timers, maps pointer/keyboard input to the
  `TextLayoutEngine`, and paints the engine's per-line render model. All scroll/caret/selection
  math lives in `FujiyNotepad.WinUI.Logic`. Paired with line-based vertical and horizontal
  scrollbars in `MainWindow`.

## Features

- Open huge files (constant memory); background line indexing with progress in the status bar.
- Mouse-wheel + scrollbar + keyboard navigation (arrows, Page Up/Down, Home/End, Ctrl+Home/End).
- Character-level selection (drag with continuous edge auto-scroll, Shift+arrows/click, double-click
  to select a word) and copy (Ctrl+C).
- **Go To Line** and a non-modal **Find** bar (Ctrl+F) with find-next (Enter/F3), progress, and cancel.
- Configurable **tab width** (Edit ▸ Tab Width) and **wide / CJK glyph** handling (two columns wide).
- Open a file from the command line / file association: `FujiyNotepad.WinUI.exe <path>`.
- *File ▸ Open Sample* generates a large sample file (with a feature-demo header) to demonstrate performance.

## Build & run

Requires the .NET 10 SDK. The Windows App Runtime is needed to run a local dev build (present on most
dev machines; otherwise install the [Windows App SDK runtime](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)).
Released builds are self-contained (Native AOT + Windows App SDK) and need nothing installed.

```powershell
dotnet build src/FujiyNotepad.WinUI.sln -c Debug
dotnet run --project src/winui/FujiyNotepad.WinUI -c Debug
```

The app is **unpackaged** (`WindowsPackageType=None`), so the produced `.exe` runs directly.

## Release

A `v*` tag publishes a self-contained **Native AOT** (`PublishAot=true`) `win-x64` build via
[`release.yml`](../../.github/workflows/release.yml): native code (no JIT), with the .NET and
Windows App SDK runtimes bundled, zipped and attached to a GitHub Release. The workflow also has a
manual `workflow_dispatch` dry-run that produces the artifact without creating a release.

## Status

The viewer is feature-complete: open → index → render → status, selection/copy, Go To Line, the
Find bar, configurable tab width, and wide-glyph handling are all in place. The engine and view
logic are covered by xUnit tests (`FujiyNotepad.Core.Tests` + `FujiyNotepad.WinUI.Logic.Tests`).
Interaction and visual details (mouse selection feel, scrollbar behavior, rendering crispness) are
best confirmed hands-on on a real desktop, since Windows App SDK UI can't be driven on headless CI.
