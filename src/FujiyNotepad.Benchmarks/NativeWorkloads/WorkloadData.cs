using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FujiyNotepad.Core;

namespace FujiyNotepad.Benchmarks.NativeWorkloads
{
    internal sealed record WorkloadManifest(
        int FormatVersion, string Id, string EncodingId, long Bytes, int Blocks, int Lines,
        long Characters, long SparseMatches, long DenseMatches, long LateOffset, string Sha256);

    internal static class WorkloadData
    {
        public const int LinesPerBlock = 4096;
        public const string SparseTerm = "SPARSE_MATCH";
        public const string DenseTerm = "DENSE_MATCH";
        public const string LateTerm = "LATE_MATCH";
        public const string Tail = "END_OF_INPUT " + LateTerm + "\n";
        public static readonly string[] Ids = ["ascii", "utf8", "utf16le", "utf32le"];

        public static TextEncoding EncodingFor(string id) => id switch
        {
            "ascii" or "utf8" => TextEncoding.Utf8,
            "utf16le" => TextEncoding.Utf16Le,
            "utf32le" => TextEncoding.Utf32Le,
            _ => throw new ArgumentException($"Unknown workload: {id}")
        };

        public static string Line(string id, int line)
        {
            string message = id == "ascii"
                ? "request completed response accepted "
                : "caf\u00e9 \u4e2d\u6587 \u65e5\u672c\u8a9e \U0001F642 request completed ";
            string fields = "2026-01-02T03:04:05Z record=" + line.ToString("D4", CultureInfo.InvariantCulture)
                + (line == 0 ? " " + SparseTerm : " ------------")
                + (line % 16 == 0 ? " " + DenseTerm : " -----------");
            return fields + " " + message + new string('x', line == LinesPerBlock - 1 ? 128 * 1024 : 40 + line % 29) + "\n";
        }

        public static void Generate(string directory, long minimumBytes)
        {
            if (minimumBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumBytes));
            }
            Directory.CreateDirectory(directory);
            foreach (string id in Ids)
            {
                TextEncoding encoding = EncodingFor(id);
                using var block = new MemoryStream();
                long blockCharacters = 0;
                for (int line = 0; line < LinesPerBlock; line++)
                {
                    string text = Line(id, line);
                    block.Write(encoding.Encode(text));
                    blockCharacters += text.Length;
                }
                byte[] bytes = block.ToArray();
                byte[] tail = encoding.Encode(Tail);
                int blocks = checked((int)Math.Max(1, (minimumBytes + bytes.Length - 1) / bytes.Length));
                long length = encoding.Bom.Length + (long)blocks * bytes.Length + tail.Length;
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
                if (drive.AvailableFreeSpace < length + (1L << 30))
                {
                    throw new IOException($"Insufficient free space for {id}: need {length} bytes plus 1 GiB headroom.");
                }
                string path = Path.Combine(directory, id + ".log");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    file.Write(encoding.Bom);
                    hash.AppendData(encoding.Bom);
                    for (int i = 0; i < blocks; i++)
                    {
                        file.Write(bytes);
                        hash.AppendData(bytes);
                    }
                    file.Write(tail);
                    hash.AppendData(tail);
                    file.Flush(flushToDisk: true);
                }
                var manifest = new WorkloadManifest(
                    1, id, encoding.Id, length, blocks, checked(blocks * LinesPerBlock + 1),
                    blocks * blockCharacters + Tail.Length, blocks, (long)blocks * (LinesPerBlock / 16),
                    encoding.Bom.Length + (long)blocks * bytes.Length + encoding.Encode("END_OF_INPUT ").Length,
                    Convert.ToHexString(hash.GetHashAndReset()));
                File.WriteAllText(Path.Combine(directory, id + ".json"),
                    JsonSerializer.Serialize(manifest, ResultsJsonContext.Default.WorkloadManifest));
                Console.WriteLine($"{id}: {length} bytes, {manifest.Lines} lines, SHA256 {manifest.Sha256}");
            }
        }

        public static WorkloadManifest Load(string directory, string id)
        {
            EncodingFor(id);
            var manifest = JsonSerializer.Deserialize(
                File.ReadAllText(Path.Combine(directory, id + ".json")), ResultsJsonContext.Default.WorkloadManifest)
                ?? throw new InvalidDataException("Missing workload manifest.");
            if (manifest.FormatVersion != 1 || manifest.Id != id || manifest.EncodingId != EncodingFor(id).Id
                || manifest.Blocks <= 0 || manifest.Lines != checked(manifest.Blocks * LinesPerBlock + 1)
                || new FileInfo(Path.Combine(directory, id + ".log")).Length != manifest.Bytes)
            {
                throw new InvalidDataException($"Invalid or mismatched manifest for {id}.");
            }
            return manifest;
        }

        public static void Verify(string directory)
        {
            foreach (string id in Ids)
            {
                WorkloadManifest manifest = Load(directory, id);
                using var file = File.OpenRead(Path.Combine(directory, id + ".log"));
                string hash = Convert.ToHexString(SHA256.HashData(file));
                if (hash != manifest.Sha256)
                {
                    throw new InvalidDataException($"Hash mismatch for {id}.");
                }
                Console.WriteLine($"Verified {id}: {hash}");
            }
        }

        public static string ExpectedLine(WorkloadManifest manifest, int line)
        {
            string text = line == manifest.Lines - 1 ? Tail : Line(manifest.Id, line % LinesPerBlock);
            TextEncoding encoding = EncodingFor(manifest.Id);
            byte[] bytes = encoding.Encode(text);
            return bytes.Length > 64 * 1024
                ? encoding.Encoding.GetString(bytes, 0, 64 * 1024) + " \u2026"
                : text.TrimEnd('\n');
        }
    }
}
