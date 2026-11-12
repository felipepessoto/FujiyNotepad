- **Safer local UI tests** — the harness stops only the viewer it launched, rejects overlapping runs and
  detected existing viewers, and restores settings only after confirming its process has exited. Failed
  restoration retains a backup and recovery instructions instead of silently losing settings (issue #199).
