# Security Policy

## Supported versions

FujiyNotepad is a desktop application released as discrete builds. Only the **latest
[release](https://github.com/felipepessoto/FujiyNotepad/releases)** receives fixes — if you are on an older
build, please update before reporting.

## Reporting a vulnerability

Please report security issues **privately**, not in a public issue:

- Use GitHub's [private vulnerability reporting](https://github.com/felipepessoto/FujiyNotepad/security/advisories/new)
  (the **Security ▸ Report a vulnerability** button on the repository).

Include the version (Help ▸ About FujiyNotepad…), your OS, and steps to reproduce. You can expect an
acknowledgement, and a fix or mitigation plan once the report is confirmed.

## Scope notes

FujiyNotepad is an **offline, read-only** file viewer:

- It does not edit or write to the files you open.
- It makes no network calls and collects no telemetry.
- The only data it writes is local: settings at `%LOCALAPPDATA%\FujiyNotepad\settings.json` and, on an
  unexpected crash, a diagnostic log at `%LOCALAPPDATA%\FujiyNotepad\crash.log`. The crash log may contain
  file paths and an exception stack trace — review it before attaching it to a public bug report.

The most relevant threat is opening a **malicious or malformed file** (unusual encodings, pathological line
lengths, huge sizes). Reports of crashes, hangs, or excessive memory use triggered by a specific input are
welcome.
