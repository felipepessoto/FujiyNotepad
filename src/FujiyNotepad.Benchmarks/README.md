# FujiyNotepad.Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org/) micro-benchmarks for the large-file engine's hot paths in
`FujiyNotepad.Core` — so a regression in the thing that makes the app special (opening and searching huge
files quickly, with little memory) shows up as a number.

This is a **manual** harness: it is not run by `dotnet test` and never gates a pull request (benchmark
numbers are noisy on shared CI runners). A separate, non-gating **Benchmarks** workflow
([`.github/workflows/benchmarks.yml`](../../.github/workflows/benchmarks.yml)) runs it on demand, when
`FujiyNotepad.Core` or the benchmarks themselves change on `master`, and weekly — saving the numbers as a
downloadable artifact (and a job summary) so a regression can be spotted and bisected over time.

The harness requires **BenchmarkDotNet 0.16.0-preview.2** for .NET 11 runtime support. The latest stable
0.15.8 can compile this project but fails before executing benchmarks because it does not recognize
`net11.0`. This prerelease dependency is confined to the non-shipping benchmark project; it does not
enable preview C# or Runtime Async in the app. Use the same harness version on both sides of a runtime
comparison, and do not treat Dry-job smoke checks or runs alongside other builds as performance evidence.

## Run

```powershell
# All benchmarks (Release is required)
dotnet run -c Release --project src/FujiyNotepad.Benchmarks

# A subset
dotnet run -c Release --project src/FujiyNotepad.Benchmarks -- --filter "*IndexLines*"
```

## What it measures

Over a fixed 600,000-line in-memory ASCII buffer (so the algorithms' CPU/allocation cost is isolated
from disk variance):

| Benchmark | Path |
| --- | --- |
| `IndexLines` | Building the sparse line index over the whole buffer — the dominant cost when opening a file. |
| `SearchFindAll` | Streaming find-all of a single late match — the chunked, vectorized byte scan. |
| `FindForwardFirst` | The synchronous bounded scan used by the line index and Go To Offset. |
| `GetLineRandom` | Random-access line retrieval (checkpoint expansion + decode) over a pre-indexed provider. |
| `GetLineRandomCold` | A fresh line provider per invocation: no decoded-line cache, but the same index and its bounded block cache. |
| `FilterDecode` | Sparse literal filtering by decoding each line. |
| `FilterByteScan` | The same filter through the byte scanner, without whole-file line decoding. |

`[MemoryDiagnoser]` is enabled, so allocations are reported alongside time.

The correctness of these same large-file paths is guarded in CI by `LargeFileIntegrationTests` in
`FujiyNotepad.Core.Tests`.

## Controlled .NET 10 / .NET 11 Native AOT comparison

The ordinary entry point above runs managed benchmarks. For **actual Native AOT**, use the preparation
and measurement script from the repository root on Windows x64 with the MSVC x64 toolchain installed:

```powershell
$Evidence = Join-Path $env:TEMP ('fujiy-aot-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
$Sdk11 = 'C:\tools\dotnet11\dotnet.exe' # Your installed, stable 11.0.100 SDK.
.\scripts\compare-native-aot.ps1 -Phase Prepare -ArtifactRoot $Evidence -DotNet11 $Sdk11

# Only after reserving a quiet machine/window and letting preparation finish:
.\scripts\compare-native-aot.ps1 -Phase Micro -ArtifactRoot $Evidence -MeasurementApproved
.\scripts\compare-native-aot.ps1 -Phase Disk -ArtifactRoot $Evidence -MeasurementApproved
```

The defaults compare source `98f6e9e99cd4379fd84be2ce2bb52d22552daa7c` using SDK **10.0.404** /
runtime+ILC **10.0.15**, and migration candidate `01de6f9886c504ab3e9bb6282aece53d1fa65b0f` using SDK
**11.0.100** / runtime+ILC **11.0.0**. Both contain the same application algorithms and shipping
dependencies. Revision and SDK-path parameters are explicit; install the stated SDKs for an exact replay.
The original `global.json` in each exported source still selects its SDK. These revisions are a comparison
baseline, **not release approval** or the later Windows App SDK servicing candidate.

Preparation exports both revisions into the evidence directory, overlays the **same current benchmark
sources and BenchmarkDotNet 0.16.0-preview.2** on both, and adapts only the two exported non-shipping
benchmark project TFMs. It never retargets the production libraries in this checkout. SDK logs, source
archives, input manifests/hashes, harness hashes, build logs/binlogs and exact commands are retained.
Do not edit an exported source or replace a native executable between preparation and measurement.

The seven original engine cases run with `--runtimes nativeaot10.0` or `nativeaot11.0` and the matching
`--ilCompilerVersion`. The harness clears BenchmarkDotNet's `IlcInstructionSet=native` and speed-preference
overrides through MSBuild arguments, preserving the SDK defaults used by the application. This includes the
intentional .NET 11 CPU-baseline change, rather than silently compiling for all features of the measurement
host. The harness also **never changes the system power plan**. Inspect the generated compiler response
files/binlogs when assessing a different SDK or BenchmarkDotNet version.

Micro measurements use five warmup iterations and ten measured iterations per process, a 250 ms target
iteration time, two process launches per runtime (separate rounds), and no outlier removal. The runtime
order alternates **AB / BA**. Keep the full JSON and measurement CSVs, not just the summary means.
Preparation's Dry jobs establish compiler/runner compatibility only; **none of their timings is evidence**.

For a coordinated pause, create `pause-measurements.json` in the evidence directory. New measurement
invocations and batch boundaries check for that marker and stop explicitly without deleting completed
results. It cannot interrupt an already running native benchmark. After obtaining a new quiet slot, remove
only that marker and repeat the measurement command with `-Resume`. Completed, validated batches are kept;
incomplete output is moved under `interrupted` before that batch is rerun. Original host snapshots remain
alongside timestamped resume snapshots. Record interruptions and the separation between paired runs when
interpreting variance.

### On-disk scenarios

`NativeWorkloads` is a separate, dependency-free, self-contained Native AOT console driver referencing the
unchanged Core project. It refuses managed execution of measurements. It is not part of the shipping app
or the ordinary BenchmarkDotNet suite.

Preparation incrementally writes four files of **at least 2 GiB each** (`-WorkloadBytes` changes this),
checking available space before each file. Reserve roughly 10 GiB for inputs and extra room for two native
build trees. Each file consists of deterministic 4,096-line blocks plus a unique late-match tail:

| Input | Content |
| --- | --- |
| `ascii` | ASCII UTF-8, no BOM. |
| `utf8` | Valid multibyte UTF-8: accented Latin, CJK, and a supplementary-plane character. |
| `utf16le` | The same multilingual records in UTF-16 LE, with a BOM. |
| `utf32le` | The same multilingual records in UTF-32 LE, with a BOM. |

Every block has one sparse match, a dense match every 16 lines, varied short-line widths, and one line
longer than the viewer's 64 KiB decode cap. The generator records exact bytes, line count, UTF-16 character
count (the application's convention), sparse/dense counts, the final match offset, and a streaming SHA256.
Inputs are shared by both runtimes and hash-verified outside scored operations.

Each process measures full indexing, sparse/dense streaming search, finding the unique late match,
sparse/dense byte filtering, sparse decode filtering, 1,000 warm/random cold line reads, 64 cold long-line
reads, and whole-file character counting. Counts and sampled decoded contents are checked; a mismatch or
unexpected filter cap fails the run. Do not increase input size until checking that dense matches remain
below the application's 2,000,000-match cap.

The OS file cache is **warm**: setup indexes the complete file, and each operation has one unscored pass
before its default three measured iterations. Two AB/BA rounds give six samples per runtime/input/operation.
`Cold` means only a fresh decoded-line cache, with the shared, bounded index-block cache; it does **not**
mean a cold OS cache. No forced collections, global cache flush, power-plan changes, or unrelated process
termination are used. Timings are wall clock; scan throughput uses the entire file size. Line-read latency
is a batch measurement, not a UI frame measurement. The tiny warm-read batches are better interpreted
alongside BenchmarkDotNet's overhead-calibrated engine case.

JSON records allocation deltas and generation collection counts for each operation. Windows memory counters
report **process-lifetime** peak private commit and peak resident working set, including setup/warmup and
earlier operations. They are not per-operation peaks. Virtual-address space is recorded separately: a lower
reservation does not prove lower resident memory. Results also identify runtime, architecture and server GC.

### Interpretation and cleanup

Record host load and variance; a shared VM is not a dedicated performance lab. Investigate repeatable
regressions before recommending acceptance. This script does not supply a universal percentage gate.
ARM64/x86, truly cold storage/startup, GUI first paint, scrolling/frame latency, and physical input require
separate hardware/desktop evidence. A locked desktop or a discovered window is not a first-paint result.
Neither SDK build speed nor console process startup is a substitute for application startup.

For artifact-size comparison, publish both exported WinUI projects with the **same** architecture, MSVC
toolchain, profile, `-p:Version=0.0.0-dev`, and pruning/ZIP procedure from the selected release workflow.
Do not compare an older default-version local ZIP with a CI candidate as if all metadata were controlled.
Retain the native executable and ZIP hashes, resolved dependencies, and evaluated publish settings.

Preserve evidence outside temporary storage before deleting generated inputs. Delete only this comparison's
named `data\ascii.log`, `data\utf8.log`, `data\utf16le.log`, and `data\utf32le.log` (and the corresponding
four files under `smoke-data`); keep their manifests, generator, native binaries and raw results for replay.
