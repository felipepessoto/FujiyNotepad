- **Safer local UI tests** — the harness stops only the viewer it launched, rejects overlapping runs and
  detected same-user viewers (including other sessions), and protects the viewer's Windows settings location
  even when environment variables are overridden. Settings are restored only after confirming its process
  has exited; failed restoration retains recovery files and instructions instead of silently losing settings
  (issue #199).
