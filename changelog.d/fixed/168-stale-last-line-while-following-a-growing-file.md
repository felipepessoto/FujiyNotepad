- **Stale last line while following a growing file** — with Follow Tail on, a background Find/count running at
  the moment the file grew could write the pre-append text of the final line back into the cache just after it
  was invalidated, leaving the view showing a truncated last line until the file next changed size (and
  indefinitely if it stopped growing). The decode is now discarded if the file changed while it was in flight
  (issue #168).
