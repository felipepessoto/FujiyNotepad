- **Diagnostic logging for silent failures** — the deliberately-swallowed exceptions behind three best-effort
  operations (the file-change watcher failing to start, a tail read failing, and indexing teardown) are now
  recorded to `%LOCALAPPDATA%\FujiyNotepad\diagnostics.log` (separate from `crash.log`, which stays reserved for
  real crashes) so a persistently-failing watcher or an unexpected fault is diagnosable instead of invisible.
  Reports are de-duplicated per site, so a per-tick failure logs once rather than every tick. No behaviour change
  (issue #143).
