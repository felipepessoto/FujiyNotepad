using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FujiyNotepad.Core;
using FujiyNotepad.TestSupport;

namespace FujiyNotepad.Core.Tests
{
    /// <summary>
    /// Multi-byte encoding hardening (issue #164). The engine searches raw bytes while the view decodes text,
    /// so a long chain of components — match alignment, whole-word boundaries, newline detection, the sparse
    /// index, BOM stripping and byte→char column mapping — all have to agree about code-unit boundaries for
    /// UTF-16 LE/BE and UTF-32. These tests exercise that chain per encoding, and where two code paths compute
    /// the same answer they are asserted against each other rather than against hard-coded expectations twice.
    /// </summary>
    public class MultiByteEncodingTests
    {
        private static readonly TextEncoding[] MultiByte =
        {
            TextEncoding.Utf16Le, TextEncoding.Utf16Be, TextEncoding.Utf32Le, TextEncoding.Utf32Be,
        };

        public static IEnumerable<object[]> MultiByteEncodings => MultiByte.Select(e => new object[] { e });

        public static IEnumerable<object[]> AllEncodings =>
            TextEncoding.All.Select(e => new object[] { e });

        private static InMemoryByteSource Source(TextEncoding encoding, string text, bool withBom = false)
        {
            byte[] body = encoding.Encoding.GetBytes(text);
            if (!withBom || encoding.Bom.Length == 0)
            {
                return new InMemoryByteSource(body);
            }

            var all = new byte[encoding.Bom.Length + body.Length];
            encoding.Bom.CopyTo(all, 0);
            body.CopyTo(all, encoding.Bom.Length);
            return new InMemoryByteSource(all);
        }

        private static async Task<(TextSearcher Searcher, LineIndexer Indexer, LineProvider Provider)> BuildAsync(
            IByteSource source, TextEncoding encoding)
        {
            var searcher = new TextSearcher(source);
            var indexer = new LineIndexer(searcher, encoding);
            await indexer.StartTaskToIndexLines(CancellationToken.None, new Progress<int>());
            return (searcher, indexer, new LineProvider(source, indexer, encoding));
        }

        private static SearchOptions OptionsFor(TextEncoding encoding, bool ignoreCase = false, bool wholeWord = false)
            => new SearchOptions
            {
                IgnoreCase = ignoreCase,
                WholeWord = wholeWord,
                UnitAlignment = encoding.CodeUnitSize,
                BigEndian = encoding.IsBigEndian,
            };

        private static async Task<List<long>> SearchAllAsync(
            TextSearcher searcher, TextEncoding encoding, string term, SearchOptions options)
        {
            var hits = new List<long>();
            await foreach (long offset in searcher.Search(0, encoding.Encode(term), options))
            {
                hits.Add(offset);
            }
            return hits;
        }

        // ----- 1. Unit alignment -----

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task Search_ByteSequenceAtNonCodeUnitOffset_IsRejected(TextEncoding encoding)
        {
            // U+4100 encodes to the same bytes as 'A' (U+0041) in the opposite order, so a run of them contains
            // the encoded 'A' pattern starting one byte in — a match that is NOT on a character boundary.
            var source = Source(encoding, new string('\u4100', 8));
            var searcher = new TextSearcher(source);

            List<long> aligned = await SearchAllAsync(searcher, encoding, "A", OptionsFor(encoding));

            Assert.Empty(aligned);
        }

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task Search_RealCharacterOnACodeUnitBoundary_IsStillFound(TextEncoding encoding)
        {
            // Guards the alignment rule from over-rejecting: the same pattern must match when genuinely present.
            var source = Source(encoding, "\u4100A\u4100");
            var searcher = new TextSearcher(source);

            List<long> hits = await SearchAllAsync(searcher, encoding, "A", OptionsFor(encoding));

            Assert.Equal(new[] { (long)encoding.CodeUnitSize }, hits);
        }

        // ----- 2. Whole word across multi-byte code units -----

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task Search_WholeWord_AsciiNeighbourIsNotMistakenForABoundary(TextEncoding encoding)
        {
            // The neighbouring 'x' has a zero high byte in every multi-byte encoding. Reading a single byte
            // instead of a whole code unit would see 0x00 and wrongly accept this as a word boundary.
            var source = Source(encoding, "xcat");
            var searcher = new TextSearcher(source);

            List<long> hits = await SearchAllAsync(searcher, encoding, "cat", OptionsFor(encoding, wholeWord: true));

            Assert.Empty(hits);
        }

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task Search_WholeWord_NonAsciiNeighbourWhoseByteLooksLikeAWordChar_IsABoundary(TextEncoding encoding)
        {
            // U+6363 is built from two 0x63 ('c') bytes, so a byte-wise boundary test would see a word character
            // and reject. The whole code unit is a CJK ideograph, which is not an ASCII word character, so this
            // IS a boundary and the match must be accepted.
            var source = Source(encoding, "\u6363cat\u6363");
            var searcher = new TextSearcher(source);

            List<long> hits = await SearchAllAsync(searcher, encoding, "cat", OptionsFor(encoding, wholeWord: true));

            Assert.Equal(new[] { (long)encoding.CodeUnitSize }, hits);
        }

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task Search_WholeWord_AtFileEdges_CountsAsABoundary(TextEncoding encoding)
        {
            var source = Source(encoding, "cat");
            var searcher = new TextSearcher(source);

            List<long> hits = await SearchAllAsync(searcher, encoding, "cat", OptionsFor(encoding, wholeWord: true));

            Assert.Equal(new[] { 0L }, hits);
        }

        // ----- 3. Surrogate pairs / astral characters -----

        [Fact]
        public async Task Utf16_AstralCharacter_IsDecodedWholeAndNotSplit()
        {
            // U+1F600 is a surrogate pair in UTF-16: it must survive decoding as both halves, in order.
            const string line = "a\U0001F600b";
            foreach (TextEncoding encoding in new[] { TextEncoding.Utf16Le, TextEncoding.Utf16Be })
            {
                var (_, _, provider) = await BuildAsync(Source(encoding, line + "\n"), encoding);

                string decoded = provider.GetLine(0);

                Assert.Equal(line, decoded);
                Assert.Equal(4, decoded.Length); // 'a', high surrogate, low surrogate, 'b'
                Assert.True(char.IsHighSurrogate(decoded[1]));
                Assert.True(char.IsLowSurrogate(decoded[2]));
            }
        }

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task ByteColumnToCharColumn_AroundAnAstralCharacter_LandsOnWholeCharacters(TextEncoding encoding)
        {
            const string line = "a\U0001F600b";
            var (_, _, provider) = await BuildAsync(Source(encoding, line + "\n"), encoding);
            string decoded = provider.GetLine(0);

            int unit = encoding.CodeUnitSize;
            int astralUnits = encoding.Encoding.GetByteCount("\U0001F600") / unit; // 2 in UTF-16, 1 in UTF-32

            Assert.Equal(0, provider.ByteColumnToCharColumn(0, 0));
            Assert.Equal(1, provider.ByteColumnToCharColumn(0, unit)); // just after 'a'

            // Just after the astral character: its char length differs from its code-unit count in UTF-32.
            int afterAstralChars = provider.ByteColumnToCharColumn(0, unit + (long)astralUnits * unit);
            Assert.Equal(1 + "\U0001F600".Length, afterAstralChars);
            Assert.True(afterAstralChars <= decoded.Length);
        }

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task ByteColumnToCharColumn_MidAstralCharacter_DoesNotExceedTheDecodedLine(TextEncoding encoding)
        {
            // A byte column landing inside a character (only reachable through a corrupt/odd offset) must clamp
            // rather than produce a column past the end of the decoded text.
            const string line = "a\U0001F600b";
            var (_, _, provider) = await BuildAsync(Source(encoding, line + "\n"), encoding);
            int lineLength = provider.GetLine(0).Length;

            for (long byteColumn = 0; byteColumn <= encoding.Encoding.GetByteCount(line) + 2; byteColumn++)
            {
                int column = provider.ByteColumnToCharColumn(0, byteColumn);
                Assert.InRange(column, 0, lineLength);
            }
        }

        // ----- 4. Byte-order marks -----

        [Theory]
        [MemberData(nameof(AllEncodings))]
        public async Task Bom_IsNotRenderedAsPartOfTheFirstLine(TextEncoding encoding)
        {
            var (_, _, provider) = await BuildAsync(Source(encoding, "alpha\nbeta\n", withBom: true), encoding);

            Assert.Equal("alpha", provider.GetLine(0));
            Assert.Equal("beta", provider.GetLine(1));
            Assert.DoesNotContain('\uFEFF', provider.GetLine(0));
        }

        [Theory]
        [MemberData(nameof(AllEncodings))]
        public async Task Bom_IsDetectedByTheEncodingDetector(TextEncoding encoding)
        {
            if (encoding.Bom.Length == 0)
            {
                return; // no BOM to detect (UTF-8 without BOM, Windows-1252)
            }

            using var source = Source(encoding, "alpha\n", withBom: true);

            TextEncoding detected = EncodingDetector.Detect(source);

            Assert.Equal(encoding.CodeUnitSize, detected.CodeUnitSize);
            Assert.Equal(encoding.IsBigEndian, detected.IsBigEndian);
        }

        // ----- 5. Malformed / truncated input -----

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task TruncatedFinalCodeUnit_DoesNotThrowAndKeepsTheIndexAligned(TextEncoding encoding)
        {
            // A file cut mid-character (a writer killed part-way, or a partial copy). The dangling byte must not
            // throw, misalign the index, or swallow the preceding lines.
            byte[] whole = encoding.Encoding.GetBytes("alpha\nbeta\ngamma");
            byte[] truncated = whole.AsSpan(0, whole.Length - 1).ToArray();
            var (_, indexer, provider) = await BuildAsync(new InMemoryByteSource(truncated), encoding);

            Assert.True(indexer.IsCompleted);
            Assert.Equal(3, provider.LineCount);
            Assert.Equal("alpha", provider.GetLine(0));
            Assert.Equal("beta", provider.GetLine(1));
            Assert.StartsWith("gamm", provider.GetLine(2)); // the final character is incomplete
        }

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task StrayOddByteBeforeContent_DoesNotThrow(TextEncoding encoding)
        {
            // Deliberately misaligned data: every subsequent code unit straddles a boundary. The viewer must
            // render something rather than fail.
            byte[] body = encoding.Encoding.GetBytes("alpha\nbeta\n");
            var shifted = new byte[body.Length + 1];
            shifted[0] = 0x21;
            body.CopyTo(shifted, 1);
            var (_, _, provider) = await BuildAsync(new InMemoryByteSource(shifted), encoding);

            for (int i = 0; i < provider.LineCount; i++)
            {
                Assert.NotNull(provider.GetLine(i)); // no throw, no infinite loop
            }
        }

        // ----- 6. Line endings encoded as multi-byte units -----

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task CrLf_EncodedAsMultiByteUnits_IsDetectedAndStripped(TextEncoding encoding)
        {
            var (_, _, provider) = await BuildAsync(Source(encoding, "alpha\r\nbeta\r\n"), encoding);

            Assert.Equal("alpha", provider.GetLine(0)); // the encoded \r must be stripped too
            Assert.Equal("beta", provider.GetLine(1));
            Assert.Equal(LineEnding.CrLf, provider.GetLineEnding(0));
        }

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task Lf_EncodedAsMultiByteUnits_IsDetected(TextEncoding encoding)
        {
            var (_, _, provider) = await BuildAsync(Source(encoding, "alpha\nbeta\n"), encoding);

            Assert.Equal(LineEnding.Lf, provider.GetLineEnding(0));
        }

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task CarriageReturnByteInsideACharacter_DoesNotSplitALine(TextEncoding encoding)
        {
            // U+0A00 contains the 0x0A ('\n') byte, and U+0D00 contains 0x0D ('\r'), but neither is a line
            // break: only a whole, aligned code unit is. A byte-wise newline scan would split these lines.
            var (_, _, provider) = await BuildAsync(Source(encoding, "a\u0A00b\u0D00c\nsecond\n"), encoding);

            Assert.Equal(2, provider.LineCount);
            Assert.Equal("a\u0A00b\u0D00c", provider.GetLine(0));
            Assert.Equal("second", provider.GetLine(1));
        }

        // ----- 7. The literal byte-scan filter must agree with the decode path -----

        private static async Task AssertFilterPathsAgreeAsync(
            TextEncoding encoding, string content, string term, bool ignoreCase, bool wholeWord)
        {
            using var source = Source(encoding, content);
            var (searcher, indexer, provider) = await BuildAsync(source, encoding);

            var (fast, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, encoding.Encode(term), OptionsFor(encoding, ignoreCase, wholeWord));

            // The decode path's predicate. This must mirror what the app actually builds, option for option —
            // a reference side that only approximates the real one turns the cross-check into a comparison of
            // two guesses. In particular CultureInvariant is not optional: FindRegexBuilder.BuildLiteralWholeWord
            // always sets it, and without it IgnoreCase folds per the ambient culture (the Turkish dotless 'i'
            // being the classic example), so this could agree or disagree depending on the machine's locale.
            StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            Func<string, bool> predicate;
            if (wholeWord)
            {
                RegexOptions options = RegexOptions.CultureInvariant;
                if (ignoreCase)
                {
                    options |= RegexOptions.IgnoreCase;
                }
                var regex = new Regex(
                    @"(?<![A-Za-z0-9_])" + Regex.Escape(term) + @"(?![A-Za-z0-9_])", options);
                predicate = line => regex.IsMatch(line);
            }
            else
            {
                predicate = line => line.Contains(term, comparison);
            }

            List<int> slow = LineFilter.Match(provider, predicate, out _);

            Assert.Equal(slow, fast);
        }

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task Filter_ByteScanAndDecodePathsAgree_Literal(TextEncoding encoding)
        {
            await AssertFilterPathsAgreeAsync(
                encoding,
                "ERROR first\nplain second\nerror third\nprefixERRORsuffix\n\u6363ERROR\u6363\nlast\n",
                "ERROR", ignoreCase: false, wholeWord: false);
        }

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task Filter_ByteScanAndDecodePathsAgree_IgnoreCase(TextEncoding encoding)
        {
            await AssertFilterPathsAgreeAsync(
                encoding,
                "ERROR first\nplain second\nerror third\nErRoR fourth\nlast\n",
                "error", ignoreCase: true, wholeWord: false);
        }

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task Filter_ByteScanAndDecodePathsAgree_WholeWord(TextEncoding encoding)
        {
            await AssertFilterPathsAgreeAsync(
                encoding,
                "ERROR first\nprefixERROR\nERRORsuffix\n ERROR \n\u6363ERROR\u6363\nlast\n",
                "ERROR", ignoreCase: false, wholeWord: true);
        }

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task Filter_ByteScanAndDecodePathsAgree_WholeWordIgnoreCase(TextEncoding encoding)
        {
            await AssertFilterPathsAgreeAsync(
                encoding,
                "ERROR first\nprefixerror\nerror suffix\n error \n\u6363Error\u6363\nlast\n",
                "error", ignoreCase: true, wholeWord: true);
        }

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task Filter_WholeWord_UsesAsciiWordCharacters_NotUnicodeWordBoundaries(TextEncoding encoding)
        {
            // The byte scanner's word test is ASCII-only (TextSearcher.IsWordCodeUnit), so a CJK ideograph next
            // to the term IS a word boundary. A regex `\b` would disagree, because .NET's \w is Unicode-aware
            // and treats U+6363 as a word character — the same filter would then return different lines
            // depending on which path ran. This pins the ASCII definition as the shared contract.
            using var source = Source(encoding, "\u6363ERROR\u6363\nplain\n");
            var (searcher, indexer, _) = await BuildAsync(source, encoding);

            var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                searcher, indexer, encoding.Encode("ERROR"), OptionsFor(encoding, wholeWord: true));

            Assert.Equal(new[] { 0 }, lines);

            // What a Unicode \b would have said, for contrast — it must NOT be what the app uses.
            Assert.DoesNotMatch(new Regex(@"\b(?:ERROR)\b"), "\u6363ERROR\u6363");
        }

        [Theory]
        [MemberData(nameof(MultiByteEncodings))]
        public async Task Filter_WholeWord_ByteScannerAndTheAsciiRegex_AgreeOnEveryNeighbourClass(TextEncoding encoding)
        {
            // The whole-word rule is implemented twice — once over raw bytes, once over decoded text — so the
            // two must agree for every KIND of neighbouring character, not just the CJK example that first
            // exposed the divergence. Several of these are cases where Unicode word semantics (\b) would have
            // disagreed, which is precisely why the app's literal builder does not use it.
            (string Neighbour, string Why)[] neighbours =
            {
                ("", "start/end of line"),
                (" ", "space"),
                (".", "punctuation"),
                ("-", "hyphen"),
                ("\u6363", "CJK ideograph - \\w would call this a word char"),
                ("\u00E9", "accented Latin - \\w would call this a word char"),
                ("\u0430", "Cyrillic - \\w would call this a word char"),
                ("a", "ASCII letter - a word char to both"),
                ("Z", "ASCII letter - a word char to both"),
                ("7", "ASCII digit - a word char to both"),
                ("_", "underscore - a word char to both"),
            };

            var asciiRegex = new Regex(@"(?<![A-Za-z0-9_])ERROR(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);

            foreach ((string neighbour, string why) in neighbours)
            {
                string line = neighbour + "ERROR" + neighbour;
                using var source = Source(encoding, line + "\n");
                var (searcher, indexer, _) = await BuildAsync(source, encoding);

                var (lines, _) = await LineFilter.MatchLinesByPatternAsync(
                    searcher, indexer, encoding.Encode("ERROR"), OptionsFor(encoding, wholeWord: true));

                bool scannerMatched = lines.Count > 0;
                bool regexMatched = asciiRegex.IsMatch(line);

                Assert.True(scannerMatched == regexMatched,
                    $"byte scanner and the app's regex disagree for neighbour '{why}': scanner={scannerMatched}, regex={regexMatched}");
            }
        }
    }
}
