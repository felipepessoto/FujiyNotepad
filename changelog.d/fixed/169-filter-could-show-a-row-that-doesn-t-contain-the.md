- **Filter could show a row that doesn't contain the search term** — when the file grew while a Filter scan was
  running (or Follow Tail restarted indexing underneath it), matches in the not-yet-indexed region were all
  attributed to the last known line, producing a bogus row — sometimes one past the end of the file — and hiding
  the real matches. The scan is now limited to the region the line index actually covers (issue #169).
