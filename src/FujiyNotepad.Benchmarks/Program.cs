using BenchmarkDotNet.Running;
using FujiyNotepad.Benchmarks;

// Run all benchmarks (or a subset via --filter). See README.md.
BenchmarkSwitcher.FromAssembly(typeof(EngineBenchmarks).Assembly).Run(args);
