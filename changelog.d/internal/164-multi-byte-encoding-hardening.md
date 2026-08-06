- **Multi-byte encoding hardening** — systematic Core coverage for UTF-16 LE/BE and UTF-32 across the whole
  chain that has to agree about code-unit boundaries: match alignment, whole-word neighbours, surrogate pairs
  through line decoding and byte→char column mapping, BOM handling, truncated/misaligned input, multi-byte line
  terminators, and the literal-filter fast path cross-checked against the per-line decode path. Uncovered the
  whole-word inconsistency fixed above (issue #164).
