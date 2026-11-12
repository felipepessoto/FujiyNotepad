- **Stricter UTF-8 auto-detection** — when no byte-order mark or UTF-16 pattern identifies the encoding,
  malformed UTF-8 now falls back to Windows-1252. Valid characters cut off by the detection sample are still
  accepted (issue #197).
