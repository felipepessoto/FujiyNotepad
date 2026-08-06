- **Index corruption from Stop then Start indexing** — *Edit > Start indexing* no longer starts a second
  indexing pass while the previous one is still stopping. Two passes writing the same index could double the
  reported line count and leave the index unsorted, which broke line lookups and could in turn trigger the
  crash above. Starting a pass while one is running is now rejected outright rather than silently corrupting
  the index (issue #166).
