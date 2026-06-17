using System.Text;
using BenchmarkDotNet.Attributes;
using FujiyNotepad.Core;
using FujiyNotepad.TestSupport;

namespace FujiyNotepad.Benchmarks
{
    /// <summary>
    /// Micro-benchmarks for the large-file engine's hot paths, so a regression in the thing that makes the
    /// app special (handling huge files quickly with little memory) shows up as a number. Inputs are a fixed
    /// in-memory buffer of numbered lines, so the benchmarks isolate the algorithms' CPU/allocation cost from
    /// disk variance. Run with: <c>dotnet run -c Release --project src/FujiyNotepad.Benchmarks</c>.
    /// </summary>
    [MemoryDiagnoser]
    public class EngineBenchmarks
    {
        // ~20 MB of varied-width ASCII lines; large enough to span thousands of index checkpoints.
        private const int LineCount = 600_000;

        private byte[] data = Array.Empty<byte>();

        // A pattern that occurs exactly once, late in the buffer (worst case for a forward scan).
        private static readonly byte[] LateNeedle = Encoding.ASCII.GetBytes("Line 599999 :");

        private LineProvider provider = null!;
        private int[] randomLines = Array.Empty<int>();

        [GlobalSetup]
        public async Task Setup()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < LineCount; i++)
            {
                sb.Append("Line ").Append(i).Append(" : the quick brown fox jumps over the lazy dog\n");
            }
            data = Encoding.ASCII.GetBytes(sb.ToString());

            // A fully-indexed provider for the random line-access benchmark.
            var source = new InMemoryByteSource(data);
            var indexer = new LineIndexer(new TextSearcher(source));
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            provider = new LineProvider(source, indexer);

            var rng = new Random(12345);
            randomLines = new int[1000];
            for (int i = 0; i < randomLines.Length; i++)
            {
                randomLines[i] = rng.Next(provider.LineCount);
            }
        }

        /// <summary>Building the sparse line index over the whole buffer — the dominant cost when opening a file.</summary>
        [Benchmark]
        public async Task IndexLines()
        {
            var indexer = new LineIndexer(new TextSearcher(new InMemoryByteSource(data)));
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
        }

        /// <summary>Streaming find-all of a single late occurrence: the chunked / vectorized byte scan end to end.</summary>
        [Benchmark]
        public async Task<long> SearchFindAll()
        {
            long last = -1;
            await foreach (long off in new TextSearcher(new InMemoryByteSource(data)).Search(0, LateNeedle))
            {
                last = off;
            }
            return last;
        }

        /// <summary>Synchronous bounded scan for the first match: the path the line index and Go To Offset use.</summary>
        [Benchmark]
        public long FindForwardFirst()
        {
            var results = new List<long>(1);
            new TextSearcher(new InMemoryByteSource(data)).FindForward(0, LateNeedle, default, 1, results);
            return results.Count > 0 ? results[0] : -1;
        }

        /// <summary>Random-access line retrieval (checkpoint expansion + decode) over the pre-indexed provider.</summary>
        [Benchmark]
        public int GetLineRandom()
        {
            int total = 0;
            foreach (int line in randomLines)
            {
                total += provider.GetLine(line).Length;
            }
            return total;
        }
    }
}
