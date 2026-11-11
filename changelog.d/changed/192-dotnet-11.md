- **.NET 11 runtime** — Move to stable .NET 11 while retaining self-contained Native AOT builds for x64,
  ARM64, and x86. Intel/AMD CPUs now need x86-64-v2-class features, including SSE4.2 and POPCNT; the Windows
  version floor and ARM64 CPU minimum are unchanged, and no separate runtime installation is needed (issue #192).
