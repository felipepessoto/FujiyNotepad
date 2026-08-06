- **Changelog notes moved to per-change files** — each pull request now adds its release note as its own file
  under `changelog.d/`, instead of every one appending to the same `[Unreleased]` section of `CHANGELOG.md`.
  Two pull requests no longer touch the same file, so a merge stops leaving every other open pull request
  conflicting over release notes. `scripts/assemble-changelog.ps1` previews what is queued and folds the
  fragments into `CHANGELOG.md` when a release is cut; CI rejects a malformed fragment on the pull request
  that adds it (issue #175).
