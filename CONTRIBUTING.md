# Contributing to FujiyNotepad

Thanks for your interest in improving FujiyNotepad — a fast, read-only viewer for very large text and log
files (WinUI 3 + Native AOT).

## Prerequisites

- The [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0) (the repo pins `11.0.100` with
  `latestMinor` roll-forward and excludes prerelease SDKs in [`global.json`](global.json)).
- Windows 10 (1809 / build 17763) or Windows 11.
- For running an unpackaged **dev** build: the
  [Windows App SDK runtime](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) (already on
  most dev machines). Released self-contained builds bundle it.
- For the AOT publish: Visual Studio 2022 or later with the **Desktop development with C++** workload
  and its default Windows SDK and compiler/linker components. ARM64 publishing also needs the ARM64 C++ tools.

## Build, test, run

```powershell
# From the repository root
dotnet build src/FujiyNotepad.slnx -c Release

# Run the app
dotnet run --project src/FujiyNotepad.WinUI -c Release

# Run the unit tests (engine + presentation; headless, no UI)
dotnet test src/FujiyNotepad.slnx -c Release -p:Platform=x64
```

Or open `src/FujiyNotepad.slnx` in Visual Studio 2026 (18.12+ for .NET 11) and press F5.
CLI builds use the MSBuild bundled with the selected .NET SDK.

### Native-AOT publish

Released builds are self-contained Native AOT. To reproduce locally:

```powershell
dotnet publish src/FujiyNotepad.WinUI/FujiyNotepad.WinUI.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64 -o publish/app
```

Use `-p:Platform=x86 -p:PublishProfile=win-x86` or
`-p:Platform=ARM64 -p:PublishProfile=win-arm64` for the other architectures. The release workflow builds
ARM64 natively on `windows-11-arm` and installs the SDK selected by `global.json` on every build runner.

### Release-package checks (no release required)

CI and the release matrix use the same conservative pruning/ZIP helper. It checks the generated
`FujiyNotepad.WinUI.pri` (not a literal `resources.pri`), framework PRIs, Win2D/runtime DLLs, the icon and the
en-US framework MUI fallback **before changing any files**. Only known-unused SDK components, debug symbols
and non-en-US framework MUI files are removed. Unknown/new runtime files, non-MUI app localization and all
en-US MUI files survive; language directories are removed only when empty. Use an explicit staging folder:
repository/filesystem roots and reparse points are rejected.

```powershell
pwsh -NoProfile -File .\scripts\test-prepare-release.ps1
.\scripts\prepare-release.ps1 -PublishDir publish\app -ZipPath dist\FujiyNotepad-local-win-x64.zip
```

The regression tests use disposable fixtures and fake the UI process boundary; they need no SDK, desktop,
app or `winapp`. Packaging refuses an existing ZIP instead of overwriting evidence.

On a disposable Windows desktop/profile, validate and exercise the **extracted final ZIP**, not the
pre-ZIP publish tree (see the UI-test safety requirements below):

```powershell
.\scripts\test-release-package.ps1 -ZipPath dist\FujiyNotepad-local-win-x64.zip -ExtractDir publish\verify -Rid win-x64 -ArtifactDir diagnostics
```

The extraction directory must be new. The driver validates required files and PE architecture, then runs the
existing UI sweep from that directory. It records source revision, ZIP SHA-256, extracted file hashes,
command results and a transcript; failures keep the ZIP and diagnostics. The x64 CI UI job and the
x64/x86/native-ARM64 release matrix use this path and upload the ZIP before subsequent validation can fail.
The ARM64 check requires a native ARM64 host; x86 runs under WOW64 on the x64 hosted runner.

For hosted validation without publishing, dispatch `release.yml` against a **branch**. Dispatches use
`0.0.0-dev` and never create a GitHub Release. Only a `v*` **tag push**, after both test and all package
validation jobs succeed, can reach the write-enabled release job. Ordinary build/test jobs have read-only
repository permissions. Downloadable Actions artifacts are not releases.

For SDK or native-dependency upgrades, publish from a clean checkout **without a preceding managed build**.
Check the generated app PRI (`FujiyNotepad.WinUI.pri`, as selected by `ProjectPriFileName`) alongside the
native executable, then exercise the published app. Missing XAML resources and CsWinRT picker/callback
marshalling failures are not ruled out by a successful managed build.

The released 11.0.100 SDK leaves `UseOptimizedPublish` **false by default** (the optimized pipeline is opt-in).
Keep that default; an optimized-publish opt-in needs separate clean XAML/PRI validation. Do not add a
fallback or alter target ordering just because older migration guidance describes a different default.

Keep the standard Native AOT instruction-set baseline, not `IlcInstructionSet=native`: x86/x64 require
x86-64-v2-class features (including SSE4.2 and POPCNT), while ARM64 retains Armv8.0-A. The
[GA compiler's baseline selection](https://github.com/dotnet/runtime/blob/v11.0.0/src/coreclr/tools/Common/InstructionSetHelpers.cs#L23-L92)
distinguishes Native AOT from the higher ReadyToRun targets. The Windows version floor and all three RIDs
are unchanged.

### UI tests

The device-free engine and presentation logic are unit-tested with xUnit. The WinUI app itself can't start
headlessly, so CI drives the **extracted final ZIP** through Windows UI Automation using the package driver
above. For a direct published-build diagnostic run:

```powershell
./scripts/ui-tests.ps1 -Exe publish/app/FujiyNotepad.WinUI.exe
```

It needs the `winapp` CLI on `PATH` (`winget install Microsoft.winappcli`).

**Close other FujiyNotepad instances for the Windows user running the tests, including other logon sessions,
and do not open any until the run finishes.** The harness checks the standard process name and the supplied
executable's name, comparing owner SIDs rather than session IDs. Viewers confirmed to belong to another
Windows user are ignored because their settings are separate. If Windows denies ownership inspection or
returns an unknown owner, the harness fails before backing up settings or launching the app rather than
guessing that the process is unrelated; sufficient permissions to inspect matching viewers are required.

This is only a preflight check: renamed viewers or ones started later can still share
`%LOCALAPPDATA%\FujiyNotepad\settings.json`. Use a disposable Windows user/profile if you cannot keep your
usual user's instances closed (ownership-inspection permissions still apply). The harness stops only its
own launched process; it never closes unrelated viewers.

Settings and guard paths use the Windows `LocalApplicationData` known folder, matching the viewer.
Overriding the `LOCALAPPDATA` environment variable does **not** isolate a UI-test run.

An exclusive `settings.json.ui-tests.lock` beside the settings file rejects overlapping harness runs before
they back up or modify settings. Setup and teardown failures still attempt cleanup. Settings are restored
byte-for-byte (or removed if originally absent) only after the owned process is confirmed exited.

If safe restoration fails, the command fails and keeps any backup and the recovery guard. The error reports
the exact recovery paths; the guard also records whether settings originally existed, the backup path and,
after launch, the owned PID. **Do not remove an active run's guard.** For an interrupted/failed run, first
confirm that its process and all other viewer instances for that Windows user are closed. Copy the backup
over `settings.json` (or remove the test-created settings file if none existed originally), then remove the
backup and guard.
A forcibly terminated harness cannot run `finally`; an incomplete guard requires inspecting the reported
files before manual recovery, not blindly deleting the guard or killing a potentially reused PID.

The lifecycle regression tests need only PowerShell 7, not a desktop, SDK, published app or `winapp`:

```powershell
pwsh -NoProfile -File .\scripts\test-ui-test-lifecycle.ps1
```

They use fake process operations and isolated temporary settings, covering exit races, teardown/setup
failures, recoverable backups, overlapping acquisition and UI-command error propagation without touching
real viewer windows or settings. UI commands fail on nonzero exit codes; the expected empty scrollbar
search is accepted only with a valid no-match JSON response.

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
  (AOT publish, shared packaging and extracted-ZIP UI Automation).
- PRs are merged with **Squash and merge**.

## Reporting bugs

Use the issue templates. If the app closed unexpectedly, attach the latest entry from
`%LOCALAPPDATA%\FujiyNotepad\crash.log`.
