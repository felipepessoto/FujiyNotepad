- **A slow regular expression can no longer freeze the app** — Find, Filter and highlight-rule patterns now
  carry a per-line time limit, so a pattern with catastrophic backtracking (e.g. `(a+)+$` meeting a long line)
  is abandoned instead of hanging. Find and Filter report *"Regex too slow"*; a highlight rule that exceeds the
  budget switches itself off rather than stalling every repaint. This mattered most for highlight rules: they
  are saved, so a bad one used to make the app unusable on every launch with no way to fix it from inside the
  app (issue #163).
