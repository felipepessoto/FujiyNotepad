# Changelog

All notable changes to FujiyNotepad are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses a `major.minor.0` release
tag per published build. Each release also has downloadable builds and notes on the
[GitHub Releases](https://github.com/felipepessoto/FujiyNotepad/releases) page.

## [Unreleased]

### Added
- **Search history** — the Find and Filter boxes remember recently used terms; press **Up / Down** in either
  box to recall them. The history persists across sessions and can be cleared from **Edit ▸ Clear Search
  History**.
- **Go To Percentage** — **Edit ▸ Go To Percentage…** jumps to a byte position by percentage (e.g. 50%),
  alongside Go To Line and Go To Offset. Works on huge files and even before indexing finishes.

### Fixed
- **No more text jitter** — the text no longer shifts up and down by a pixel as the caret blinks. The line
  height is now snapped to a whole device pixel so every line lands on the physical pixel grid, instead of
  being re-rasterized a pixel off between redraws.
- **Timestamp delta** now recognizes the `yyyy-MM-dd HH:mm:ss,SSS` log format — a comma before the
  milliseconds, as emitted by log4j and Python `logging` — and keeps sub-second precision, so two events
  within the same second no longer report a `0` delta.

### Internal
- A BenchmarkDotNet harness over the engine's hot paths plus a large-file integration test, guarding the
  large-file performance and memory behaviour.
- `TimestampParser`'s fixed patterns now use the source-generated `[GeneratedRegex]` engine (the
  Native-AOT-recommended path); behaviour is unchanged and the published-size impact is negligible (~15 KB).

## [4.8.0] - 2026-06-17

### Added
- **Bookmarks** — toggle lines (Ctrl+F2) and jump between them (F2 / Shift+F2), with ticks in a new
  scrollbar marker margin alongside find-match positions.
- **Theme override** — System / Light / Dark, plus **High Contrast** support on the Win2D text surface.
- **Show Whitespace** — spaces, tabs, trailing-space runs, and control characters as markers; the status bar
  shows the line-ending style (LF / CRLF / Mixed).
- **Filter export** — copy or save just the lines matching the current filter.
- **Timestamp delta** — selecting two timestamped log lines shows the elapsed time between them in the
  status bar.
- **Quick actions** — Copy File Path, Reveal in Explorer, and Copy with Line Numbers.
- **Crash diagnostics** — unhandled exceptions are written to `%LOCALAPPDATA%\FujiyNotepad\crash.log`.
- A richer **Open Sample** that showcases the features above.

### Changed
- Accessibility, async, and theming best-practices pass; modernized the find / filter / status surfaces.

### Internal
- App-layer UI tests drive the published app through UI Automation in CI; coverage is shown in the CI
  summary and gated at 85% line coverage. Refactors toward a device-free presentation layer.

## [4.7.0] - 2026-06-16

### Added
- Persistent **highlight rules** (colour text by pattern).
- **Filter / grep view** (show only matching lines).
- Line-ending type (LF / CRLF / Mixed) in the status bar.

## [4.6.0] - 2026-06-16

### Added
- **Sparse line index** — ~1024× less index memory for huge files.
- Reload the current file (F5) with a file-changed hint.
- Open shortcut (Ctrl+O), Go To Offset, and whole-word find in UTF-16 / UTF-32.

## [4.5.0] - 2026-06-15

### Added
- Selection length in the status bar.
- Line-number gutter (View ▸ Line Numbers).
- Open files that another process is still writing, and fail gracefully.

## [4.4.0] - 2026-06-15

### Added
- View options: monospace font choice, zoom, and a status-bar character count.
- Help ▸ About dialog.
- Character encoding support (UTF-16 / UTF-32 / ANSI) with auto-detection.

---

For releases **v4.3.0 and earlier**, see the
[GitHub Releases](https://github.com/felipepessoto/FujiyNotepad/releases) page.

[Unreleased]: https://github.com/felipepessoto/FujiyNotepad/compare/v4.8.0...HEAD
[4.8.0]: https://github.com/felipepessoto/FujiyNotepad/compare/v4.7.0...v4.8.0
[4.7.0]: https://github.com/felipepessoto/FujiyNotepad/compare/v4.6.0...v4.7.0
[4.6.0]: https://github.com/felipepessoto/FujiyNotepad/compare/v4.5.0...v4.6.0
[4.5.0]: https://github.com/felipepessoto/FujiyNotepad/compare/v4.4.0...v4.5.0
[4.4.0]: https://github.com/felipepessoto/FujiyNotepad/compare/v4.3.0...v4.4.0
