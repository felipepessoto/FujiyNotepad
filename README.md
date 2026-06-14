# FujiyNotepad

[![CI](https://github.com/felipepessoto/FujiyNotepad/actions/workflows/ci.yml/badge.svg)](https://github.com/felipepessoto/FujiyNotepad/actions/workflows/ci.yml)

A lightweight Windows text viewer (WPF) built to open and navigate **very large text files**
that would be slow or impossible to load in a conventional editor.

Instead of reading the whole file into memory, FujiyNotepad reads it on demand in
chunks (via the positional `RandomAccess` API) and draws only the lines currently on
screen with a **custom virtualized text view** (built from scratch on `FormattedText`,
not a `TextBox`). Scrolling, paging, *Go To Line*, and *Find* stream directly from disk
using a vectorized byte search (`ReadOnlySpan<byte>.IndexOf`), so memory usage stays
roughly constant regardless of file size.

## Download

Grab the latest build from the [Releases page](https://github.com/felipepessoto/FujiyNotepad/releases).
Each release publishes two single-file builds (no installer):

- **`FujiyNotepad-<version>-win-x64-self-contained.exe`** — larger (~130 MB); bundles the
  .NET runtime, so it runs on a clean machine with **no prerequisites**.
- **`FujiyNotepad-<version>-win-x64.exe`** — tiny (~0.2 MB); requires the
  [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to be installed.

Both are unsigned, so Windows SmartScreen may warn on first run (*More info → Run anyway*).

## Features

- Open multi-gigabyte text files with near-constant memory usage — a custom virtualized
  text view draws only the lines currently on screen, reading each on demand.
- Native-style navigation: mouse wheel, a line-based vertical scrollbar, a horizontal
  scrollbar, and the keyboard (arrows, `PageUp`/`PageDown`, `Home`/`End`, `Ctrl+Home`/`Ctrl+End`).
- Character-level **selection** (mouse drag, `Shift`+click/arrows) and **copy** (`Ctrl+C`),
  with a caret and an `Ln, Col` indicator in the status bar.
- Background **line indexing** with progress reporting and cancellation; the scrollbar
  extent grows as the indexer discovers lines.
- **Go To Line** (`Ctrl+G`) and **Find text** (`Ctrl+F`) with progress and cancellation,
  highlighting the match in place.
- Tabs are expanded to tab stops; *Open Sample* generates a large (~10M line) file to
  demonstrate performance.

## Requirements

- Windows 10 or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build
- .NET Desktop Runtime 10 to run

## Build & run

```powershell
# From the repository root
dotnet build src/FujiyNotepad.sln -c Release
dotnet run --project src/FujiyNotepad.UI -c Release
```

Or open `src/FujiyNotepad.sln` in Visual Studio 2022 (17.8+) and press F5.

## Project layout

| Path | Description |
| --- | --- |
| `src/FujiyNotepad.UI` | WPF application (UI + core viewing/search logic). |
| `src/FujiyNotepad.UI/Model/IByteSource.cs` | Read-only positional byte access; `FileByteSource` is backed by `RandomAccess`. |
| `src/FujiyNotepad.UI/Model/TextSearcher.cs` | Chunked, vectorized forward/backward byte search over an `IByteSource`. |
| `src/FujiyNotepad.UI/Model/LineIndexer.cs` | Builds and caches line-start offsets for *Go To Line*, the view, and Find. |
| `src/FujiyNotepad.UI/Model/LineProvider.cs` | Decodes individual lines on demand (with caching) from the byte source. |
| `src/FujiyNotepad.UI/Controls/TextView.cs` | Custom virtualized text surface: rendering, scrolling, caret, and selection. |
| `src/FujiyNotepad.UI/Controls/TextColumns.cs` | Tab expansion and character/column mapping for the view. |
| `src/FujiyNotepad.UI/FujiyTextBox.xaml(.cs)` | Hosts `TextView` plus the scrollbars and wires Find/Go To. |

## License

See [LICENSE](LICENSE).
