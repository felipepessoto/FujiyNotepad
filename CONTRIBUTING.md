# Contributing to FujiyNotepad

Thanks for your interest in improving FujiyNotepad — a fast, read-only viewer for very large text and log
files (WinUI 3 + Native AOT).

## Prerequisites

- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the repo pins `10.0.100` with
  `latestMinor` roll-forward in [`global.json`](global.json)).
- Windows 10 (1809 / build 17763) or Windows 11.
- For running an unpackaged **dev** build: the
  [Windows App SDK runtime](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) (already on
  most dev machines). Released self-contained builds bundle it.
- Optional, for the AOT publish: the **Desktop development with C++** workload (the ILC linker uses it).

## Build, test, run

```powershell
# From the repository root
dotnet build src/FujiyNotepad.slnx -c Release

# Run the app
dotnet run --project src/FujiyNotepad.WinUI -c Release

# Run the unit tests (engine + presentation; headless, no UI)
dotnet test src/FujiyNotepad.slnx -c Release -p:Platform=x64
```

Or open `src/FujiyNotepad.slnx` in Visual Studio 2022 (17.13+, for the `.slnx` solution format) and press F5.

### Native-AOT publish

Released builds are self-contained Native AOT. To reproduce locally:

```powershell
dotnet publish src/FujiyNotepad.WinUI/FujiyNotepad.WinUI.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64 -o publish/app
```

The app's nastiest bugs (CsWinRT picker marshalling, a missing app PRI) only reproduce in the published
AOT build, so test UI-affecting changes against a publish, not just the managed run.

Before testing the release layout, remove unused SDK components with the same script that CI and releases use:

```powershell
.\scripts\prepare-release.ps1 -PublishDir publish\app
.\scripts\test-prepare-release.ps1
```

The generated app PRI is currently `FujiyNotepad.WinUI.pri`; keep it beside the executable along with the
framework PRIs, native runtime DLLs, `Assets\FujiyNotepad.ico`, and the framework's `en-US` MUI fallback.
The app's English and Portuguese strings are compiled into its PRI, independently of framework MUI pruning.
The release workflow runs the UI sweep against the extracted, pruned ZIP on x64, x86, and native ARM64.

Windows App SDK servicing is independent of .NET: the app uses stable **2.6.5** on **.NET 10**, with
**Win2D 1.4.0** unchanged. No `XamlOptionalChanges` style optimizations or experimental System Composition
Engine are enabled. When updating the SDK, review its
[release notes](https://learn.microsoft.com/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0?pivots=stable),
recheck the pruning list against the actual native package layout, and test picker focus, clipboard, themes,
localization, and rendering on the published executable. Record Windows/CPU/display coverage gaps; shared-runner
timings are observations, not performance benchmarks.

### UI tests

The device-free engine and presentation logic are unit-tested with xUnit. The WinUI app itself can't start
headlessly, so it is covered by an app-layer UI test that drives the **published** executable through Windows
UI Automation:

```powershell
./scripts/ui-tests.ps1 -Exe publish/app/FujiyNotepad.WinUI.exe
```

It needs the `winapp` CLI on `PATH` (`winget install Microsoft.winappcli`).

### Benchmarks

`FujiyNotepad.Benchmarks` is a [BenchmarkDotNet](https://benchmarkdotnet.org/) harness over the engine's hot
paths (indexing, search, line retrieval). Run it on demand in Release:

```powershell
dotnet run -c Release --project src/FujiyNotepad.Benchmarks
```

It never gates a PR; a separate, non-gating workflow records the numbers over time for regression tracking —
see [the project's README](src/FujiyNotepad.Benchmarks/README.md) for details. The same large-file paths are
guarded for correctness in CI by `LargeFileIntegrationTests`.

## Architecture

The code is layered so almost all logic is testable without a graphics device:

| Project | Role |
| --- | --- |
| `src/FujiyNotepad.Core` | UI-agnostic engine: positional file I/O, chunked/vectorized byte search, the sparse background line index, on-demand line decode. |
| `src/FujiyNotepad.Presentation` | Framework-independent view logic: scroll/caret/selection, the per-line render model, find/filter/highlight/bookmarks, settings, status-bar formatting. No Win2D/WinUI dependency. |
| `src/FujiyNotepad.WinUI` | The WinUI 3 app shell: the Win2D `TextCanvas` and `MainWindow` (menus, scrollbars, status bar, dialogs). |
| `*.Tests` + `FujiyNotepad.TestSupport` | xUnit tests and shared fakes. |
| `src/FujiyNotepad.Benchmarks` | BenchmarkDotNet harness over the engine's hot paths (manual; not run in CI). |

When adding logic, prefer putting it in `Core` or `Presentation` (device-free, unit-testable) with a thin
wiring layer in `FujiyNotepad.WinUI`.

## Conventions

- **Native AOT safety:** avoid runtime code generation and reflection on hot/serialization paths — use
  `System.Text.Json` source generation for settings, and the **interpreted** `Regex` engine (`new Regex(…)`)
  for the Find and highlight-rule patterns, which are supplied by the user at runtime. (`RegexOptions.Compiled`
  emits IL at runtime and is unavailable under AOT; the source-generated `[GeneratedRegex]` engine *is*
  AOT-friendly but only applies to patterns fixed at compile time — which is what `TimestampParser` uses for
  its fixed log-timestamp patterns.)
- **C# style:** block-scoped namespaces; non-underscore private field names in app/engine code (e.g.
  `provider`, `currentEncoding`); PascalCase types/methods/properties; `Async` suffix on async methods.
- **Accessibility:** set `AutomationProperties.AutomationId`/`Name` on interactive controls.
- **Tests:** the CI coverage gate requires Core + Presentation line coverage to stay at or above **85%**.

## Pull requests

- Branch off an up-to-date `master`; keep PRs focused.
- CI must be green: the **Build & Test** job (build, tests, 85% coverage gate) and the **UI tests** job
  (AOT publish + UI Automation).
- PRs are merged with **Squash and merge**.

## Reporting bugs

Use the issue templates. If the app closed unexpectedly, attach the latest entry from
`%LOCALAPPDATA%\FujiyNotepad\crash.log`.
