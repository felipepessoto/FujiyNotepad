using System.Text;
using FujiyNotepad.Core;

namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>Shared builders for engine tests: file-free <see cref="LineProvider"/> instances.</summary>
    internal static class TestData
    {
        /// <summary>Builds a fully-indexed <see cref="LineProvider"/> over in-memory ASCII content.</summary>
        public static async Task<LineProvider> BuildProviderAsync(string content)
        {
            var source = new InMemoryByteSource(content);
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            return new LineProvider(source, indexer);
        }

        /// <summary>Produces <paramref name="count"/> newline-terminated lines of varied-width text.</summary>
        public static string ManyLines(int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                sb.Append("Line ").Append(i).Append(" : the quick brown fox\n");
            }
            return sb.ToString();
        }

        /// <summary>Produces <paramref name="count"/> identical newline-terminated lines of <paramref name="text"/>.</summary>
        public static string RepeatLines(string text, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                sb.Append(text).Append('\n');
            }
            return sb.ToString();
        }
    }
}
