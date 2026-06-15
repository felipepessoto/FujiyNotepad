using System.Text;

namespace FujiyNotepad.Core
{
    /// <summary>
    /// A text encoding the viewer can read, bundling the .NET <see cref="System.Text.Encoding"/> with the
    /// byte-level facts the chunked pipeline needs: the encoded newline / carriage-return sequences (for line
    /// splitting and terminator stripping), the byte-order mark, and the code-unit size (for aligning matches
    /// to character boundaries in multi-byte encodings). UTF-8 keeps a single-byte newline and an empty/3-byte
    /// BOM, so the UTF-8 path is byte-for-byte the original behaviour.
    /// </summary>
    public sealed class TextEncoding
    {
        /// <summary>Stable identifier used for settings persistence and the encoding menu.</summary>
        public string Id { get; }

        /// <summary>Human-readable name shown in the menu / status bar.</summary>
        public string DisplayName { get; }

        public Encoding Encoding { get; }

        /// <summary>The byte-order mark this codec writes, or empty. Stripped from line 0 when the file begins with it.</summary>
        public byte[] Bom { get; }

        /// <summary>The encoded "\n" (one code unit) — what line splitting searches for.</summary>
        public byte[] NewLineBytes { get; }

        /// <summary>The encoded "\r" — stripped (when present) before a line's trailing newline.</summary>
        public byte[] CarriageReturnBytes { get; }

        /// <summary>
        /// Bytes per code unit (1 for UTF-8 / single-byte, 2 for UTF-16, 4 for UTF-32). A real character — and
        /// therefore a real newline or literal match — only begins at a file offset that is a multiple of this.
        /// </summary>
        public int CodeUnitSize => NewLineBytes.Length;

        /// <summary>
        /// True for the big-endian multi-byte codecs (UTF-16 BE / UTF-32 BE). The whole-word Find check needs
        /// it to combine a neighbour code unit's bytes into the right value; it is meaningless (and false) for
        /// the single-byte encodings.
        /// </summary>
        public bool IsBigEndian { get; }

        private TextEncoding(string id, string displayName, Encoding encoding, byte[] bom, bool isBigEndian = false)
        {
            Id = id;
            DisplayName = displayName;
            Encoding = encoding;
            Bom = bom;
            IsBigEndian = isBigEndian;
            NewLineBytes = encoding.GetBytes("\n");
            CarriageReturnBytes = encoding.GetBytes("\r");
        }

        /// <summary>Encodes <paramref name="text"/> to bytes (no BOM) — used to build a literal Find pattern.</summary>
        public byte[] Encode(string text) => Encoding.GetBytes(text);

        public static readonly TextEncoding Utf8 = new("utf-8", "UTF-8", new UTF8Encoding(false), Array.Empty<byte>());
        public static readonly TextEncoding Utf8Bom = new("utf-8-bom", "UTF-8 with BOM", new UTF8Encoding(false), new byte[] { 0xEF, 0xBB, 0xBF });
        public static readonly TextEncoding Utf16Le = new("utf-16le", "UTF-16 LE", new UnicodeEncoding(false, false), new byte[] { 0xFF, 0xFE });
        public static readonly TextEncoding Utf16Be = new("utf-16be", "UTF-16 BE", new UnicodeEncoding(true, false), new byte[] { 0xFE, 0xFF }, isBigEndian: true);
        public static readonly TextEncoding Utf32Le = new("utf-32le", "UTF-32 LE", new UTF32Encoding(false, false), new byte[] { 0xFF, 0xFE, 0x00, 0x00 });
        public static readonly TextEncoding Utf32Be = new("utf-32be", "UTF-32 BE", new UTF32Encoding(true, false), new byte[] { 0x00, 0x00, 0xFE, 0xFF }, isBigEndian: true);
        public static readonly TextEncoding Windows1252 = new("windows-1252", "Windows-1252 (ANSI)", Windows1252Encoding.Instance, Array.Empty<byte>());

        /// <summary>All encodings offered in the menu, in display order.</summary>
        public static readonly IReadOnlyList<TextEncoding> All = new[]
        {
            Utf8, Utf8Bom, Utf16Le, Utf16Be, Utf32Le, Utf32Be, Windows1252,
        };

        /// <summary>Looks up an encoding by <see cref="Id"/>, or returns <see cref="Utf8"/> when unknown/null.</summary>
        public static TextEncoding FromId(string? id)
        {
            foreach (TextEncoding e in All)
            {
                if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return e;
                }
            }
            return Utf8;
        }
    }
}
