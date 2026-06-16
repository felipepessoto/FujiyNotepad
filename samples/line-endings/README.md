# Line-ending sample files

Small files for manually testing FujiyNotepad's **line-ending indicator** — the status-bar label that
shows whether a file uses **LF**, **CRLF**, or **Mixed** newlines. Open each one and confirm the status
bar (next to the encoding) shows the expected label.

| File | Encoding | Line-ending label |
| --- | --- | --- |
| `lf.txt` | UTF-8 | **LF** |
| `crlf.txt` | UTF-8 | **CRLF** |
| `mixed.txt` | UTF-8 | **Mixed** |
| `no-newline.txt` | UTF-8 | *(blank — no newline in the file)* |
| `utf16le-crlf.txt` | UTF-16 LE | **CRLF** |
| `utf16le-lf.txt` | UTF-16 LE | **LF** |
| `utf16be-crlf.txt` | UTF-16 BE | **CRLF** |

The detection runs in the file's encoding, so the UTF-16 variants exercise matching the encoded `\n` /
`\r` sequences on code-unit boundaries (the high byte of an ASCII character is `00`, not a newline).

## Things to try

1. **LF vs CRLF** — open `lf.txt` then `crlf.txt`; the indicator switches between **LF** and **CRLF**.
2. **Mixed** — open `mixed.txt`; because it contains both `\n` and `\r\n` lines, the indicator shows
   **Mixed**.
3. **No newline** — open `no-newline.txt`; with no newline to classify, the indicator is blank.
4. **Encoding-aware** — open `utf16le-crlf.txt` (status bar shows *UTF-16 LE* + **CRLF**). Then pick a
   different encoding from the *Encoding* menu and back to *Auto-detect*; the line-ending label is
   re-detected for the active encoding.

> These are tiny fixtures (tens of bytes) and are committed as `binary` (see `.gitattributes`) so their
> exact CRLF/LF bytes and BOMs are preserved — git must not normalize them.
