# Changelog

All notable changes to FujiyNotepad are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses a `major.minor.0` release
tag per published build. Each release also has downloadable builds and notes on the
[GitHub Releases](https://github.com/felipepessoto/FujiyNotepad/releases) page.

## [Unreleased]

### Internal
- **Less GC pressure on the search hot path** — the ~1 MiB byte buffer used to scan the file for every Find,
  Find Previous, line-index expansion and filter pass is now rented from a shared pool instead of allocated
  each call. That buffer was a Large Object Heap allocation, so reusing it removes the per-operation LOH
  allocations and the Gen 2 garbage collections they triggered (measured ~1 MB/op → a few hundred bytes/op on
  the engine benchmarks), with no change in throughput or behaviour.

## [4.10.0] - 2026-06-19

### Added
- **Highlight occurrences of the selected text** — selecting a word (or any single-line run of text) now subtly
  highlights every other occurrence of it in view, so you can eyeball where a token recurs without running a
  search. It's a single-line, length-bounded reading aid that stands aside while Find is active (Find wins), and
  you can turn it off any time with **View ▸ Highlight Selection Occurrences** (the setting persists).
- **Word wrap** — **View ▸ Word Wrap** wraps long lines to the window width instead of scrolling sideways
  (the horizontal scrollbar hides while it's on). It keeps the constant-memory model — the file still scrolls
  by source line, so even multi-gigabyte files wrap instantly — and the setting persists across sessions.
- **Reopen last file on startup** — launching with no file now reopens the file you had open, scrolled back to
  where you left it (first visible line and caret), so resuming a big log is instant. Toggle it off any time
  with **File ▸ Reopen Last File on Startup** (which also forgets the remembered file). Closing a file
  explicitly with **File ▸ Close** (Ctrl+W) also clears it, so an intentionally-closed file isn't reopened —
  only closing the window resumes it next time.
- **Screen reader support for file content** — the text surface now exposes the caret line's text to Narrator
  and other assistive tech through UI Automation, reading each line aloud (via a UIA notification) as you move
  through the file with the arrow keys, and announcing the line/column on focus. Previously the viewer could be
  focused but its content was opaque to screen readers.

### Fixed
- **Text stays rock-steady on caret blink** — the caret is now drawn as a separate composition overlay above
  the text surface, so the blink toggles only the caret and no longer repaints (and re-rasterizes) the text.
  This makes the long-standing "text shifts up/down by a pixel as the caret blinks" problem *structurally*
  impossible — at any display scale (it was most visible at 150%) and regardless of future layout changes —
  rather than relying on every redraw staying perfectly pixel-aligned. Line heights and tops are still snapped
  to whole physical pixels to keep the text crisp.

### Changed
- **Faster open for very large files** — the exact character count is no longer computed with a full-file
  decode every time you open a multi-gigabyte file. Above ~256 MB the status bar shows the file size with a
  **Count characters** action you can click when you actually want the exact total. Single-byte encodings
  (Windows-1252) still show the count instantly, since it equals the byte count.

### Internal
- **Localization** — user-facing strings now resolve from resource tables (`Strings/<lang>/Resources.resw`)
  instead of being hardcoded: the menu bar, Find/Filter bars, status-bar links, and all dialogs bind via
  `x:Uid` or a new AOT-safe `LocalizedStrings` (`ResourceLoader`) helper. A **Brazilian Portuguese (pt-BR)**
  translation ships as a worked example; set the `FUJIY_LANG` environment variable (e.g. `pt-BR`) to preview a
  language without changing Windows. English is the default/fallback — no visible change for English users.

## [4.9.0] - 2026-06-19

### Added
- **Search history** — the Find and Filter boxes remember recently used terms; press **Up / Down** in either
  box to recall them. The history persists across sessions and can be cleared from **Edit ▸ Clear Search
  History**.
- **Go To Percentage** — **Edit ▸ Go To Percentage…** jumps to a byte position by percentage (e.g. 50%),
  alongside Go To Line and Go To Offset. Works on huge files and even before indexing finishes.
- **Highlight presets** — the Highlight Rules dialog has an **Insert preset** menu with ready-made rule sets
  (Log levels, Web access log, JSON, Timestamps & IDs) you can drop in and then tweak.
- **Incremental find** — the Find bar highlights matches and shows the match count **as you type** (debounced),
  honouring the match-case / whole-word / regex toggles; Enter and F3 still jump between matches.
- **Follow Tail** — **View ▸ Follow Tail** live-tails a growing file (logs): appended lines are pulled in
  automatically and the view sticks to the end while you're at the bottom (scroll up to read history without
  being yanked). The status bar shows **Following**.
- **Open at a line from the command line** — `FujiyNotepad app.log --line 1234 --column 7` (or a trailing
  `app.log:1234:7`) opens the file and jumps to that location, so build output / grep / stack traces can link
  straight in. The drive colon in a Windows path is not mistaken for a line separator.
- **Read piped input** — `tool | FujiyNotepad -` reads standard input and shows it as it streams in, with
  **Follow Tail** on so the view keeps up with a live producer. The piped data is spooled to a temporary file
  (the viewer needs random access) which is removed automatically when the window closes.

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
