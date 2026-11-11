using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FujiyNotepad.Core;

namespace FujiyNotepad.Benchmarks.NativeWorkloads
{
    internal static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "generate")
            {
                WorkloadData.Generate(args[1], long.Parse(args[2], CultureInfo.InvariantCulture));
                return 0;
            }
            if (args.Length == 2 && args[0] == "verify")
            {
                WorkloadData.Verify(args[1]);
                return 0;
            }
            if (args.Length == 5 && args[0] == "run")
            {
                int iterations = int.Parse(args[4], CultureInfo.InvariantCulture);
                if (iterations < 1 || iterations > 100)
                {
                    throw new ArgumentOutOfRangeException(nameof(iterations), "Use 1-100 measured iterations.");
                }
                if (RuntimeFeature.IsDynamicCodeSupported)
                {
                    throw new InvalidOperationException("Run the published Native AOT executable, not the managed build.");
                }
                RunResult result = await RunAsync(args[1], args[2], iterations);
                using var output = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write);
                JsonSerializer.Serialize(output, result, ResultsJsonContext.Default.RunResult);
                return 0;
            }
            Console.Error.WriteLine("Commands: generate <directory> <minimum-bytes> | verify <directory> | run <directory> <ascii|utf8|utf16le|utf32le> <result.json> <iterations>");
            return 2;
        }

        private static async Task<RunResult> RunAsync(string directory, string id, int iterations)
        {
            WorkloadManifest manifest = WorkloadData.Load(directory, id);
            TextEncoding encoding = WorkloadData.EncodingFor(id);
            using var source = new FileByteSource(Path.Combine(directory, id + ".log"));
            var searcher = new TextSearcher(source);
            var options = new SearchOptions { UnitAlignment = encoding.CodeUnitSize, BigEndian = encoding.IsBigEndian };
            var indexer = new LineIndexer(searcher, encoding);
            await indexer.StartTaskToIndexLines(CancellationToken.None, NoProgress.Instance);
            var provider = new LineProvider(source, indexer, encoding);
            Check(manifest.Lines, provider.LineCount, "setup line count");

            var random = new Random(12345);
            int[] randomLines = Enumerable.Range(0, 1000).Select(_ => random.Next(manifest.Lines)).ToArray();
            int[] longLines = Enumerable.Range(0, 64)
                .Select(i => (i % manifest.Blocks) * WorkloadData.LinesPerBlock + WorkloadData.LinesPerBlock - 1).ToArray();
            long expectedRandomLength = 0;
            long expectedLongLength = 0;
            foreach (int line in randomLines)
            {
                string expected = WorkloadData.ExpectedLine(manifest, line);
                if (provider.GetLine(line) != expected)
                {
                    throw new InvalidDataException($"Decoded content mismatch on {id} line {line}.");
                }
                expectedRandomLength += expected.Length;
            }
            foreach (int line in longLines)
            {
                string expected = WorkloadData.ExpectedLine(manifest, line);
                if (provider.GetLine(line) != expected)
                {
                    throw new InvalidDataException($"Long-line content mismatch on {id} line {line}.");
                }
                expectedLongLength += expected.Length;
            }

            byte[] sparse = encoding.Encode(WorkloadData.SparseTerm);
            byte[] dense = encoding.Encode(WorkloadData.DenseTerm);
            byte[] late = encoding.Encode(WorkloadData.LateTerm);
            var operations = new (string Name, long Expected, int LinesPerOperation, Func<Task<long>> Action)[]
            {
                ("IndexLines", manifest.Lines, 0, async () =>
                {
                    var fresh = new LineIndexer(searcher, encoding);
                    await fresh.StartTaskToIndexLines(CancellationToken.None, NoProgress.Instance);
                    return new LineProvider(source, fresh, encoding).LineCount;
                }),
                ("SearchSparse", manifest.SparseMatches, 0, () => CountMatchesAsync(sparse)),
                ("SearchDense", manifest.DenseMatches, 0, () => CountMatchesAsync(dense)),
                ("FindForwardFirst", manifest.LateOffset, 0, () =>
                {
                    var matches = new List<long>(1);
                    searcher.FindForward(0, late, options, 1, matches);
                    return Task.FromResult(matches.Count == 1 ? matches[0] : -1);
                }),
                ("FilterSparse", manifest.SparseMatches, 0, () => FilterAsync(sparse)),
                ("FilterDense", manifest.DenseMatches, 0, () => FilterAsync(dense)),
                ("FilterDecode", manifest.SparseMatches, 0, () =>
                {
                    List<int> matches = LineFilter.Match(provider,
                        line => line.Contains(WorkloadData.SparseTerm, StringComparison.Ordinal), out bool capped);
                    if (capped)
                    {
                        throw new InvalidDataException("Decode filter unexpectedly reached its match cap.");
                    }
                    return Task.FromResult((long)matches.Count);
                }),
                ("GetLineRandom", expectedRandomLength, randomLines.Length,
                    () => Task.FromResult(ReadLines(provider, randomLines))),
                ("GetLineRandomCold", expectedRandomLength, randomLines.Length,
                    () => Task.FromResult(ReadLines(new LineProvider(source, indexer, encoding), randomLines))),
                ("GetLongLinesCold", expectedLongLength, longLines.Length,
                    () => Task.FromResult(ReadLines(new LineProvider(source, indexer, encoding), longLines))),
                ("CountCharacters", manifest.Characters, 0, () => CharacterCounter.CountAsync(source, encoding))
            };
            var samples = new List<Sample>();
            foreach (var operation in operations)
            {
                // Native code has no JIT warmup. This pass warms file pages, pools and operation-specific caches.
                Check(operation.Expected, await operation.Action(), operation.Name + " warmup");
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    DateTimeOffset started = DateTimeOffset.UtcNow;
                    long allocated = GC.GetTotalAllocatedBytes(precise: true);
                    int gen0 = GC.CollectionCount(0);
                    int gen1 = GC.CollectionCount(1);
                    int gen2 = GC.CollectionCount(2);
                    long start = Stopwatch.GetTimestamp();
                    long value = await operation.Action();
                    double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                    int gen0Collections = GC.CollectionCount(0) - gen0;
                    int gen1Collections = GC.CollectionCount(1) - gen1;
                    int gen2Collections = GC.CollectionCount(2) - gen2;
                    Check(operation.Expected, value, operation.Name);
                    var sample = new Sample(operation.Name, iteration, started, milliseconds, value,
                        operation.LinesPerOperation,
                        operation.LinesPerOperation == 0 ? manifest.Bytes / (1024.0 * 1024) / (milliseconds / 1000) : null,
                        allocatedBytes, gen0Collections, gen1Collections, gen2Collections, NativeMemory.Snapshot());
                    samples.Add(sample);
                    Console.WriteLine(FormattableString.Invariant($"{id} {operation.Name} [{iteration}]: {milliseconds:F4} ms, {allocatedBytes} allocated bytes"));
                }
            }
            using var process = Process.GetCurrentProcess();
            return new RunResult(1, Environment.Version.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.OSDescription, RuntimeFeature.IsDynamicCodeSupported, GCSettings.IsServerGC,
                "OS file cache warmed by full indexing and one unscored invocation per operation; no cache flush. Cold = fresh LineProvider only; shared index block cache. No forced GC.",
                "Memory peaks are OS process-lifetime high-water marks including setup/warmup, not per-operation peaks. Private commit is not resident working set; virtual bytes are address space.",
                manifest, samples, NativeMemory.Snapshot(), process.VirtualMemorySize64);

            async Task<long> CountMatchesAsync(byte[] pattern)
            {
                long count = 0;
                await foreach (long _ in searcher.Search(0, pattern, options))
                {
                    count++;
                }
                return count;
            }

            async Task<long> FilterAsync(byte[] pattern)
            {
                (List<int> lines, bool capped) = await LineFilter.MatchLinesByPatternAsync(searcher, indexer, pattern, options);
                if (capped)
                {
                    throw new InvalidDataException("Byte filter unexpectedly reached its match cap.");
                }
                return lines.Count;
            }
        }

        private static long ReadLines(LineProvider provider, int[] lines)
        {
            long total = 0;
            foreach (int line in lines)
            {
                total += provider.GetLine(line).Length;
            }
            return total;
        }

        private static void Check(long expected, long actual, string operation)
        {
            if (expected != actual)
            {
                throw new InvalidDataException($"{operation}: expected {expected}, got {actual}.");
            }
        }

        private sealed class NoProgress : IProgress<int>
        {
            public static readonly NoProgress Instance = new();
            public void Report(int value) { }
        }
    }

    internal sealed record MemorySnapshot(ulong PrivateBytes, ulong PeakPrivateBytes, ulong WorkingSetBytes, ulong PeakWorkingSetBytes);
    internal sealed record Sample(string Operation, int Iteration, DateTimeOffset StartedUtc, double Milliseconds,
        long Value, int LinesPerOperation, double? MiBPerSecond, long AllocatedBytes, int Gen0, int Gen1, int Gen2, MemorySnapshot Memory);
    internal sealed record RunResult(int FormatVersion, string Runtime, string Architecture, string OS, bool DynamicCodeSupported,
        bool ServerGC, string CachePolicy, string MemoryPolicy, WorkloadManifest Workload, List<Sample> Samples,
        MemorySnapshot ProcessMemory, long VirtualBytes);

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(WorkloadManifest))]
    [JsonSerializable(typeof(RunResult))]
    internal partial class ResultsJsonContext : JsonSerializerContext { }

    internal static partial class NativeMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Counters
        {
            public uint Size;
            public uint PageFaultCount;
            public nuint PeakWorkingSetSize;
            public nuint WorkingSetSize;
            public nuint QuotaPeakPagedPoolUsage;
            public nuint QuotaPagedPoolUsage;
            public nuint QuotaPeakNonPagedPoolUsage;
            public nuint QuotaNonPagedPoolUsage;
            public nuint PagefileUsage;
            public nuint PeakPagefileUsage;
            public nuint PrivateUsage;
        }

        [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
        private static partial int GetProcessMemoryInfo(nint process, ref Counters counters, uint size);

        public static MemorySnapshot Snapshot()
        {
            var counters = new Counters { Size = (uint)Marshal.SizeOf<Counters>() };
            if (GetProcessMemoryInfo(new nint(-1), ref counters, counters.Size) == 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }
            return new MemorySnapshot(counters.PrivateUsage, counters.PeakPagefileUsage,
                counters.WorkingSetSize, counters.PeakWorkingSetSize);
        }
    }
}
