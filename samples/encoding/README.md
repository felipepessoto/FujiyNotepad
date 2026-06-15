# Encoding sample files

Small files for manually testing FujiyNotepad's character-encoding support (auto-detection, the
**Encoding** menu, and find). Every file holds the same text in a different encoding. Open each one
and confirm the status bar / *Encoding* menu shows the expected encoding and the text renders
correctly (no mojibake).

| File | Encoding | How it's detected |
| --- | --- | --- |
| `utf8.txt` | UTF-8 (no BOM) | valid UTF-8 |
| `utf8-bom.txt` | UTF-8 with BOM | BOM `EF BB BF` |
| `utf16le-bom.txt` | UTF-16 LE | BOM `FF FE` |
| `utf16be-bom.txt` | UTF-16 BE | BOM `FE FF` |
| `utf16le-nobom.txt` | UTF-16 LE (no BOM) | heuristic (NUL bytes at odd offsets) |
| `utf16be-nobom.txt` | UTF-16 BE (no BOM) | heuristic (NUL bytes at even offsets) |
| `utf32le-bom.txt` | UTF-32 LE | BOM `FF FE 00 00` |
| `utf32be-bom.txt` | UTF-32 BE | BOM `00 00 FE FF` |
| `windows1252.txt` | Windows-1252 (ANSI) | not valid UTF-8 → fallback |

## What correct output looks like

The Unicode files should show, across several lines:

- **Accents:** `café, naïve, Köln, señor, façade, Ærø`
- **Punctuation:** curly quotes `“ ”`, apostrophes `‘ ’`, an em‑dash `—`, an ellipsis `…`, `€100`, `£50`, `½`, `©`, `®`
- **CJK:** `日本語 — 中文 — 한국어`
- **Symbols:** `☺ ♥ → ∞ ✓`
- **Find test:** `needle needle NEEDLE needle`

`windows1252.txt` shows the same accents and punctuation (these all exist in Windows-1252) but not the
CJK / symbol line. If it were mis‑decoded as Latin‑1 the em‑dash, curly quotes, ellipsis and `€` would
appear as control characters — a good way to confirm the Windows‑1252 path.

## Things to try

1. **Auto-detection** — open each file; the *Encoding* menu shows *Auto-detect* ticked and the status
   bar names the detected encoding.
2. **Manual override** — open `utf16le-bom.txt`, then pick **UTF-8** from the *Encoding* menu: the text
   turns to mojibake. Pick **Auto-detect** to restore it.
3. **Find** — with any file open, search for `needle` (`Ctrl+F`): the count is **4** and all matches are
   highlighted, regardless of encoding. Try *Match case* (`NEEDLE` is excluded) and *Regex* too.

> These are tiny fixtures (a few hundred bytes). The line endings are CRLF, so they also exercise the
> per-encoding carriage-return stripping.
