# Changelog fragments

Release notes live here, **one file per change**, instead of in a shared `[Unreleased]` section.

The reason is mechanical: every pull request used to append a bullet to the same place in `CHANGELOG.md`, so
each merge left every other open pull request conflicting. In one batch of six pull requests that meant several
rounds of hand-merging a list of bullets — and one of those hand-merges dropped an entry before it was caught.
Two pull requests never touch the same file here, so the conflict cannot happen.

## Adding an entry

Create a file named `changelog.d/<category>/<issue-or-pr-number>-<short-slug>.md`:

```text
changelog.d/fixed/169-filter-shows-non-matching-row.md
```

Categories are the folder names — `added`, `changed`, `deprecated`, `removed`, `fixed`, `security`, `internal`.
Starting the file name with the issue or PR number keeps the release notes in a sensible order and guarantees
two pull requests never pick the same name.

The file holds the **finished Markdown bullet**, exactly as it should appear in the changelog:

```markdown
- **Filter could show a row that doesn't contain the search term** — when the file grew while a Filter scan was
  running, matches in the not-yet-indexed region were all attributed to the last known line, producing a bogus
  row and hiding the real matches. The scan is now limited to the region the line index covers (issue #169).
```

It is copied verbatim at release time — not reformatted — so what you write is what ships. Keep the existing
house style: a bold summary, an em dash, then what changed and why it mattered, ending with the issue number.

Write for someone reading the release notes, not for the reviewer of your pull request: describe the effect on
using the app, not the implementation. `internal` is the place for changes with no user-visible effect.

## Seeing what's queued

```powershell
./scripts/assemble-changelog.ps1 -Preview
```

Because `[Unreleased]` no longer lists entries inline, this is how to read what the next release will contain.

## Cutting a release

```powershell
./scripts/assemble-changelog.ps1 -Version 4.13.0
```

That inserts a `## [4.13.0] - <date>` section built from the fragments and deletes them, as one step of the
release pull request. Empty categories are omitted. Anything a contributor left inline in `[Unreleased]` by
hand is carried into the release too, with a warning — losing a note is worse than an unconventional one.
