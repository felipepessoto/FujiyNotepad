# FujiyNotepad.Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org/) micro-benchmarks for the large-file engine's hot paths in
`FujiyNotepad.Core` — so a regression in the thing that makes the app special (opening and searching huge
files quickly, with little memory) shows up as a number.

This is a **manual** harness: it is not run by `dotnet test` and never gates a pull request (benchmark
numbers are noisy on shared CI runners). A separate, non-gating **Benchmarks** workflow
([`.github/workflows/benchmarks.yml`](../../.github/workflows/benchmarks.yml)) runs it on demand, when
`FujiyNotepad.Core` or the benchmarks themselves change on `master`, and weekly — saving the numbers as a
downloadable artifact (and a job summary) so a regression can be spotted and bisected over time.

## Run

```powershell
# All benchmarks (Release is required)
dotnet run -c Release --project src/FujiyNotepad.Benchmarks

# A subset
dotnet run -c Release --project src/FujiyNotepad.Benchmarks -- --filter "*IndexLines*"
```

## What it measures

Over a fixed ~20 MB in-memory buffer of numbered lines (so the algorithms' CPU/allocation cost is isolated
from disk variance):

| Benchmark | Path |
| --- | --- |
| `IndexLines` | Building the sparse line index over the whole buffer — the dominant cost when opening a file. |
| `SearchFindAll` | Streaming find-all of a single late match — the chunked, vectorized byte scan. |
| `FindForwardFirst` | The synchronous bounded scan used by the line index and Go To Offset. |
| `GetLineRandom` | Random-access line retrieval (checkpoint expansion + decode) over a pre-indexed provider. |

`[MemoryDiagnoser]` is enabled, so allocations are reported alongside time.

The correctness of these same large-file paths is guarded in CI by `LargeFileIntegrationTests` in
`FujiyNotepad.Core.Tests`.
