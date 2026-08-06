<#
.SYNOPSIS
    Assembles per-change changelog fragments into CHANGELOG.md.

.DESCRIPTION
    Every pull request drops its release note into changelog.d/<category>/<name>.md instead of editing the
    shared [Unreleased] section, so two pull requests never touch the same file and never conflict over the
    changelog. This script folds those fragments into CHANGELOG.md when a release is cut.

    A fragment holds the finished Markdown bullet, exactly as it should appear - including the leading "- "
    and any continuation indentation. Assembly is therefore a copy, not a reformat: what you write is what
    ships. Its category is the folder it sits in; its file name is free-form, but starting with the issue or
    PR number keeps the release notes in a sensible order and makes collisions between two PRs impossible.

.PARAMETER Preview
    Prints the section that would be produced, and changes nothing. Use this to see what is queued for the
    next release, since [Unreleased] no longer lists it inline.

.PARAMETER Check
    Validates the fragments (known category, non-empty, looks like a bullet) and returns a non-zero exit code
    if any are wrong. Intended for CI, so a malformed fragment is caught on the PR that adds it rather than
    on release day.

.PARAMETER Version
    Cuts the release: inserts a "## [<Version>] - <date>" section built from the fragments, then deletes them.

.PARAMETER Date
    Release date for the new section. Defaults to today (yyyy-MM-dd).

.EXAMPLE
    ./scripts/assemble-changelog.ps1 -Preview

.EXAMPLE
    ./scripts/assemble-changelog.ps1 -Version 4.13.0
#>
[CmdletBinding(DefaultParameterSetName = 'Preview')]
param(
    [Parameter(ParameterSetName = 'Preview')][switch]$Preview,
    [Parameter(ParameterSetName = 'Check')][switch]$Check,
    [Parameter(ParameterSetName = 'Release', Mandatory = $true)][string]$Version,
    [Parameter(ParameterSetName = 'Release')][string]$Date
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$changelogPath = Join-Path $repoRoot 'CHANGELOG.md'
$fragmentRoot = Join-Path $repoRoot 'changelog.d'

# Keep a Changelog's categories plus this project's "Internal" bucket, in the order they are rendered.
$categoryOrder = @('added', 'changed', 'deprecated', 'removed', 'fixed', 'security', 'internal')
$categoryTitles = @{
    added = 'Added'; changed = 'Changed'; deprecated = 'Deprecated'; removed = 'Removed'
    fixed = 'Fixed'; security = 'Security'; internal = 'Internal'
}

# Sorts numerically when a name starts with digits (so 9-… precedes 10-…), alphabetically otherwise, giving
# a stable order that does not depend on the file system's enumeration.
function Get-SortKey([string]$name) {
    if ($name -match '^(\d+)') { return @(0, [int]$matches[1], $name) }
    return @(1, 0, $name)
}

function Get-Fragments {
    $result = [ordered]@{}
    if (-not (Test-Path $fragmentRoot)) { return $result }

    foreach ($category in $categoryOrder) {
        $dir = Join-Path $fragmentRoot $category
        if (-not (Test-Path $dir)) { continue }

        $files = Get-ChildItem -Path $dir -Filter '*.md' -File |
            Sort-Object { (Get-SortKey $_.Name)[0] }, { (Get-SortKey $_.Name)[1] }, { (Get-SortKey $_.Name)[2] }

        if ($files.Count -gt 0) { $result[$category] = $files }
    }
    return $result
}

function Test-Fragments {
    $problems = @()
    if (-not (Test-Path $fragmentRoot)) { return $problems }

    # A stray .md at the top level has no category and would be silently dropped at release time.
    foreach ($stray in Get-ChildItem -Path $fragmentRoot -Filter '*.md' -File) {
        if ($stray.Name -ne 'README.md') {
            $problems += "$($stray.Name): sits directly in changelog.d/. Move it into one of: $($categoryOrder -join ', ')."
        }
    }

    foreach ($dir in Get-ChildItem -Path $fragmentRoot -Directory) {
        if ($categoryOrder -notcontains $dir.Name) {
            $problems += "changelog.d/$($dir.Name)/: unknown category. Use one of: $($categoryOrder -join ', ')."
            continue
        }

        foreach ($file in Get-ChildItem -Path $dir.FullName -File) {
            $rel = "changelog.d/$($dir.Name)/$($file.Name)"
            if ($file.Extension -ne '.md') {
                $problems += "${rel}: not a .md file."
                continue
            }

            $text = [System.IO.File]::ReadAllText($file.FullName)
            if ([string]::IsNullOrWhiteSpace($text)) {
                $problems += "${rel}: is empty."
            }
            elseif ($text.TrimStart() -notmatch '^- ') {
                $problems += "${rel}: must start with a Markdown bullet ('- '), because it is copied verbatim into the release section."
            }
        }
    }

    return $problems
}

function Format-Section {
    param([hashtable]$Fragments)

    # Built with explicit "`n" rather than AppendLine: AppendLine emits Environment.NewLine, which is CRLF on
    # Windows, so the section would carry a different line ending from the rest of the pipeline and the final
    # LF-to-CRLF conversion would turn those into CR CR LF. Line endings are applied once, at write time.
    $parts = [System.Collections.Generic.List[string]]::new()
    foreach ($category in $categoryOrder) {
        if (-not $Fragments.Contains($category)) { continue }

        $parts.Add("### $($categoryTitles[$category])")
        foreach ($file in $Fragments[$category]) {
            $text = ([System.IO.File]::ReadAllText($file.FullName) -replace "`r`n", "`n").TrimEnd()
            $parts.Add($text)
        }
        $parts.Add('')
    }
    return ($parts -join "`n").TrimEnd()
}

# ---------------------------------------------------------------------------------------------------------

$problems = Test-Fragments
if ($problems.Count -gt 0) {
    Write-Host "Changelog fragment problems:" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    if ($Check -or $PSCmdlet.ParameterSetName -eq 'Release') { exit 1 }
}
elseif ($Check) {
    $count = (Get-Fragments).Values | ForEach-Object { $_.Count } | Measure-Object -Sum
    Write-Host "Changelog fragments OK ($([int]$count.Sum) queued)." -ForegroundColor Green
    exit 0
}

$fragments = Get-Fragments
$section = Format-Section -Fragments $fragments

if ($PSCmdlet.ParameterSetName -ne 'Release') {
    if ([string]::IsNullOrWhiteSpace($section)) {
        Write-Host "No changelog fragments queued."
    }
    else {
        # Write-Output, not Write-Host, so the preview can be piped, redirected or diffed.
        Write-Output $section
    }
    exit 0
}

# --- Release -----------------------------------------------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($section)) {
    throw "No changelog fragments found in changelog.d/ - nothing to release."
}

$releaseDate = if ($Date) { $Date } else { (Get-Date).ToString('yyyy-MM-dd') }

# Normalise to LF for processing, but remember what the file actually uses so it can be written back the
# same way. Without this the whole file is rewritten with different line endings, which buries the real
# change in a diff of every line - and does it again on every release.
$originalText = [System.IO.File]::ReadAllText($changelogPath)
$usesCrLf = $originalText.Contains("`r`n")
$content = $originalText -replace "`r`n", "`n"

$marker = "## [Unreleased]"
$markerIndex = $content.IndexOf($marker)
if ($markerIndex -lt 0) { throw "Could not find '$marker' in CHANGELOG.md." }

# Everything from the marker to the next '## [' heading is the Unreleased block. Anything a contributor left
# in there by hand is carried into the release rather than dropped - losing someone's note is far worse than
# an unconventional entry.
$afterMarker = $markerIndex + $marker.Length
$nextHeading = $content.IndexOf("`n## [", $afterMarker)
if ($nextHeading -lt 0) { throw "Could not find the previous release heading after '$marker'." }

$unreleasedBody = $content.Substring($afterMarker, $nextHeading - $afterMarker)
$strays = ($unreleasedBody -split "`n" | Where-Object { $_.TrimStart().StartsWith('- ') })
if ($strays.Count -gt 0) {
    Write-Warning "[Unreleased] still contains $($strays.Count) inline bullet(s); they will be carried into $Version. Prefer changelog.d/ fragments so concurrent PRs do not conflict."
    $section = ($unreleasedBody.Trim() + "`n`n" + $section).Trim()
}

# Built by joining with an explicit "`n" rather than as a here-string: a here-string picks up the line
# endings of THIS script file, which would leak CRLF into the LF-normalised pipeline and become CR CR LF
# after the conversion at write time.
$placeholder = (@(
    ''
    ''
    '<!-- Release notes are not written here. Each change adds its own file under changelog.d/<category>/, so'
    '     concurrent pull requests never touch the same file. See changelog.d/README.md, and run'
    '     ./scripts/assemble-changelog.ps1 -Preview to see what is queued for the next release. -->'
    ''
) -join "`n")

$newContent = $content.Substring(0, $afterMarker) +
    $placeholder +
    "`n## [$Version] - $releaseDate`n`n" +
    $section + "`n" +
    $content.Substring($nextHeading)

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
if ($usesCrLf) { $newContent = $newContent -replace "`n", "`r`n" }
[System.IO.File]::WriteAllText($changelogPath, $newContent, $utf8NoBom)

$removed = 0
foreach ($category in $fragments.Keys) {
    foreach ($file in $fragments[$category]) {
        Remove-Item $file.FullName -Force
        $removed++
    }
}

Write-Host "Wrote [$Version] - $releaseDate to CHANGELOG.md and removed $removed fragment(s)." -ForegroundColor Green
