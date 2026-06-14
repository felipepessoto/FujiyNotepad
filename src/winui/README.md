# FujiyNotepad — WinUI 3 edition

A WinUI 3 (Windows App SDK) port of FujiyNotepad's large-file viewer. Like the WPF edition,
it opens **multi-gigabyte text files without loading them into memory** — only the lines
currently on screen are read from disk and drawn.

## Architecture

The UI-agnostic engine lives in **`FujiyNotepad.Core`** and is shared by (and identical in
behavior to) the WPF edition's engine:

| Project | Description |
| --- | --- |
| `src/FujiyNotepad.Core` | Engine: `FileByteSource` (positional `RandomAccess` I/O), `TextSearcher` (chunked, vectorized byte search), `LineIndexer` (background line-offset index), `LineProvider` (decode a line on demand), `LineColumns` (tab/column mapping). No UI dependency. |
| `src/FujiyNotepad.Core.Tests` | xUnit tests for the engine (run headless, no UI). |
| `src/winui/FujiyNotepad.WinUI` | The WinUI 3 app. |

The view is a **custom text surface drawn with [Win2D](https://github.com/microsoft/Win2D)**
(`CanvasControl` + DirectWrite), not a `TextBox`/`ListView`. WinUI has no text control that
virtualizes over a file (its virtualization seam lives only in the items controls), so the
viewer is drawn from scratch:

- `Controls/TextCanvas.cs` — draws only the visible lines, owns its scroll state (first visible
  line + horizontal offset), and implements the caret, character-level selection, copy, and
  keyboard/mouse navigation itself. Paired with line-based vertical and horizontal scrollbars
  in `MainWindow`.

## Features

- Open huge files (constant memory); background line indexing with progress in the status bar.
- Mouse-wheel + scrollbar + keyboard navigation (arrows, Page Up/Down, Home/End, Ctrl+Home/End).
- Character-level selection (drag, Shift+arrows/click) and copy (Ctrl+C).
- **Go To Line** and **Find** (with in-place highlight).
- Open a file from the command line / file association: `FujiyNotepad.WinUI.exe <path>`.
- *File ▸ Open Sample* generates a large sample file to demonstrate performance.

## Build & run

Requires the .NET 10 SDK and the Windows App Runtime (already present on most dev machines;
otherwise install the [Windows App SDK runtime](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)).

```powershell
dotnet build src/FujiyNotepad.WinUI.sln -c Debug
dotnet run --project src/winui/FujiyNotepad.WinUI -c Debug
```

The app is **unpackaged** (`WindowsPackageType=None`), so the produced `.exe` runs directly.

## Status

This is an initial port. Build is clean (0 warnings) and the engine has 28 passing tests.
The open → index → render → status pipeline is validated end-to-end. Interaction and visual
details (mouse selection feel, scrollbar behavior, rendering crispness) still warrant a
hands-on check on a real desktop. Known iteration-1 limits mirror the WPF view: monospace
assumption, fixed tab size (4), horizontal extent tracks the widest visible line.
