# FujiyNotepad

A lightweight Windows text viewer (WPF) built to open and navigate **very large text files**
that would be slow or impossible to load in a conventional editor.

Instead of reading the whole file into memory, FujiyNotepad reads it on demand in
chunks (via the positional `RandomAccess` API) and renders only the lines currently
visible in the viewport. Scrolling, paging, *Go To Line*, and *Find* all stream
directly from disk using a vectorized byte search (`ReadOnlySpan<byte>.IndexOf`), so
memory usage stays roughly constant regardless of file size.

## Features

- Open multi-gigabyte text files with near-constant memory usage (virtualized viewport).
- Background **line indexing** with progress reporting and cancellation.
- **Go To Line** (`Ctrl+G`).
- **Find text** (`Ctrl+F`) with progress and cancellation.
- Keyboard navigation: arrows, `PageUp`/`PageDown`, `Ctrl+Home`/`Ctrl+End`.
- *Open Sample* generates a large (~10M line) file to demonstrate performance.

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
| `src/FujiyNotepad.UI/Model/LineIndexer.cs` | Builds and caches line-start offsets for *Go To Line*. |
| `src/FujiyNotepad.UI/FujiyTextBox.xaml(.cs)` | Virtualized text control that renders only visible lines. |

## License

See [LICENSE](LICENSE).
