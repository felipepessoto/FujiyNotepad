<#
.SYNOPSIS
    Self-test for scripts/assemble-changelog.ps1.

.DESCRIPTION
    The assemble script rewrites CHANGELOG.md, so it is worth checking rather than trusting. Each case runs
    against a throwaway sandbox.

    Two rules this file follows, both learned the hard way.

    Every path is ABSOLUTE. PowerShell's Push-Location does not change .NET's current directory, so a
    [System.IO.File] call with a relative path silently hits the real repository instead of the sandbox -
    which, while this was being written, quietly wrote test data into the real CHANGELOG.md five times before
    it was noticed. The last case asserts the repository's own changelog is untouched, so that cannot recur
    silently.

    Sandboxes SEED their own fragments rather than copying changelog.d/ from the branch. A release pull
    request has just consumed every fragment, so a suite that copied them would find none, -Version would
    throw, and CI would fail on the release itself - the worst possible moment. Test data must not depend on
    what happens to be queued.
#>
param([string]$Repo = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$results = @()

function New-Sandbox {
    param(
        [string]$Repo,
        [switch]$UseLf,
        [string[]]$Categories = @('fixed', 'internal')
    )

    $dir = Join-Path $env:TEMP ("clog-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path (Join-Path $dir 'scripts') -Force | Out-Null
    Copy-Item (Join-Path $Repo 'scripts\assemble-changelog.ps1') (Join-Path $dir 'scripts')

    $fragmentRoot = Join-Path $dir 'changelog.d'
    New-Item -ItemType Directory -Path $fragmentRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $fragmentRoot 'README.md'), "# Changelog fragments`r`n", $utf8)
    foreach ($c in $Categories) {
        $sub = Join-Path $fragmentRoot $c
        New-Item -ItemType Directory -Path $sub -Force | Out-Null
        [IO.File]::WriteAllText(
            (Join-Path $sub "100-$c-sample.md"),
            "- **Sample $c entry** first line of the body.`r`n  and a continuation line (issue #100).`r`n",
            $utf8)
    }

    # The changelog itself comes from the repository: its real structure is what is being edited.
    $text = [IO.File]::ReadAllText((Join-Path $Repo 'CHANGELOG.md'))
    if ($UseLf) { $text = $text -replace "`r`n", "`n" }
    [IO.File]::WriteAllText((Join-Path $dir 'CHANGELOG.md'), $text, $utf8)

    return $dir
}

function Invoke-Assemble {
    param([string]$Sandbox, [hashtable]$Params)
    # Hashtable splatting: array splatting would pass these positionally, and the script takes named parameters.
    & (Join-Path $Sandbox 'scripts\assemble-changelog.ps1') @Params *>&1 | Out-Null
    return $LASTEXITCODE
}

function Get-Endings([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    $crlf = 0; $lf = 0
    for ($i = 0; $i -lt $b.Length; $i++) {
        if ($b[$i] -eq 10) { if ($i -gt 0 -and $b[$i - 1] -eq 13) { $crlf++ } else { $lf++ } }
    }
    $doubleCr = ([regex]::Matches([IO.File]::ReadAllText($path), "`r`r")).Count
    return [pscustomobject]@{ CrLf = $crlf; Lf = $lf; DoubleCr = $doubleCr }
}

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    $script:results += [pscustomobject]@{ Test = $name; Ok = $ok; Detail = $detail }
}

# Returns just the section for $Version: from its heading to the NEXT "## [" heading. Deliberately does not
# name the following release - hard-coding "## [4.12.0]" would silently start swallowing extra sections as
# soon as the project ships another release.
function Get-ReleaseSection {
    param([string]$Text, [string]$Version)
    $start = $Text.IndexOf("## [$Version]")
    if ($start -lt 0) { return '' }
    $next = $Text.IndexOf("`n## [", $start + 1)
    if ($next -lt 0) { return $Text.Substring($start) }
    return $Text.Substring($start, $next - $start)
}

# 1. A CRLF changelog stays CRLF, with no CR CR LF.
$s = New-Sandbox -Repo $Repo
Invoke-Assemble $s @{ Version = '4.13.0'; Date = '2026-08-06' } | Out-Null
$e = Get-Endings (Join-Path $s 'CHANGELOG.md')
Add-Result 'CRLF file stays CRLF' ($e.Lf -eq 0 -and $e.DoubleCr -eq 0) "CRLF=$($e.CrLf) LF=$($e.Lf) CRCR=$($e.DoubleCr)"
Remove-Item $s -Recurse -Force

# 2. An LF changelog stays LF.
$s = New-Sandbox -Repo $Repo -UseLf
Invoke-Assemble $s @{ Version = '4.13.0'; Date = '2026-08-06' } | Out-Null
$e = Get-Endings (Join-Path $s 'CHANGELOG.md')
Add-Result 'LF file stays LF' ($e.CrLf -eq 0 -and $e.DoubleCr -eq 0) "CRLF=$($e.CrLf) LF=$($e.Lf) CRCR=$($e.DoubleCr)"
Remove-Item $s -Recurse -Force

# 3. Release consumes the fragments, writes the section, and keeps the pointer.
$s = New-Sandbox -Repo $Repo
$before = @(Get-ChildItem (Join-Path $s 'changelog.d') -Recurse -File -Filter *.md | Where-Object Name -ne 'README.md').Count
$exit = Invoke-Assemble $s @{ Version = '4.13.0'; Date = '2026-08-06' }
$after = @(Get-ChildItem (Join-Path $s 'changelog.d') -Recurse -File -Filter *.md | Where-Object Name -ne 'README.md').Count
$text = [IO.File]::ReadAllText((Join-Path $s 'CHANGELOG.md'))
$ok = ($exit -eq 0) -and ($after -eq 0) -and ($text -match '## \[4\.13\.0\] - 2026-08-06') -and ($text -match 'changelog\.d/<category>/')
Add-Result 'Release consumes fragments' $ok "exit=$exit fragments $before -> $after"
Remove-Item $s -Recurse -Force

# 4. No stray-bullet warning when [Unreleased] holds only the pointer comment.
$s = New-Sandbox -Repo $Repo
$warn = & (Join-Path $s 'scripts\assemble-changelog.ps1') -Version 4.13.0 -Date 2026-08-06 3>&1 2>$null |
    Where-Object { $_ -is [System.Management.Automation.WarningRecord] }
Add-Result 'No false stray-bullet warning' ($null -eq $warn) "warnings: $(($warn | ForEach-Object { $_.Message }) -join '; ')"
Remove-Item $s -Recurse -Force

# 5. A hand-written inline bullet is carried into the release - and the pointer comment is NOT.
$s = New-Sandbox -Repo $Repo
$p = Join-Path $s 'CHANGELOG.md'
$t = [IO.File]::ReadAllText($p) -replace '(?s)(## \[Unreleased\])', "`$1`r`n`r`n### Added`r`n- **Hand-written note** left here out of habit (issue #999)."
[IO.File]::WriteAllText($p, $t, $utf8)
$injected = ([IO.File]::ReadAllText($p)) -match 'Hand-written note'
Invoke-Assemble $s @{ Version = '4.13.0'; Date = '2026-08-06' } | Out-Null
$afterText = [IO.File]::ReadAllText($p)
$carried = $afterText -match 'Hand-written note'
$releaseBody = Get-ReleaseSection -Text $afterText -Version '4.13.0'
$commentLeaked = $releaseBody -match 'Release notes are not written here'
Add-Result 'Inline bullet carried, comment not leaked' ($injected -and $carried -and -not $commentLeaked) "carried=$carried commentLeaked=$commentLeaked"
Remove-Item $s -Recurse -Force

# 6. Malformed fragments are rejected and the changelog is left alone.
$s = New-Sandbox -Repo $Repo
[IO.File]::WriteAllText((Join-Path $s 'changelog.d\stray.md'), "- misplaced", $utf8)
New-Item -ItemType Directory -Path (Join-Path $s 'changelog.d\bogus') | Out-Null
[IO.File]::WriteAllText((Join-Path $s 'changelog.d\bogus\1-x.md'), "- wrong category", $utf8)
[IO.File]::WriteAllText((Join-Path $s 'changelog.d\fixed\900-empty.md'), "", $utf8)
[IO.File]::WriteAllText((Join-Path $s 'changelog.d\fixed\901-prose.md'), "no bullet here", $utf8)
$snapshot = [IO.File]::ReadAllText((Join-Path $s 'CHANGELOG.md'))
$checkExit = Invoke-Assemble $s @{ Check = $true }
$relExit = Invoke-Assemble $s @{ Version = '4.13.0' }
$untouched = ([IO.File]::ReadAllText((Join-Path $s 'CHANGELOG.md')) -eq $snapshot)
Add-Result 'Malformed rejected, changelog untouched' (($checkExit -ne 0) -and ($relExit -ne 0) -and $untouched) "check=$checkExit release=$relExit untouched=$untouched"
Remove-Item $s -Recurse -Force

# 7. Sections come out in the documented order, and empty categories are omitted. The sandbox seeds exactly
#    the categories asserted here, so the expectation cannot drift with whatever the branch has queued.
$s = New-Sandbox -Repo $Repo -Categories @('internal', 'added', 'security', 'changed')
Invoke-Assemble $s @{ Version = '4.13.0'; Date = '2026-08-06' } | Out-Null
$afterText = [IO.File]::ReadAllText((Join-Path $s 'CHANGELOG.md'))
$releaseBody = Get-ReleaseSection -Text $afterText -Version '4.13.0'
$headings = @($releaseBody -split "`r?`n" | Where-Object { $_ -match '^### ' })
$expected = @('### Added', '### Changed', '### Security', '### Internal')
$ok = ("$($headings -join '|')" -eq "$($expected -join '|')")
Add-Result 'Sections ordered, empty ones omitted' $ok "got: $($headings -join ' ')"
Remove-Item $s -Recurse -Force

# 8. Fragment text is copied verbatim, including a Markdown hard line break (two trailing spaces).
$s = New-Sandbox -Repo $Repo
[IO.File]::WriteAllText((Join-Path $s 'changelog.d\fixed\200-hardbreak.md'), "- **Hard break** first line.  `r`n  second line (issue #200).`r`n", $utf8)
Invoke-Assemble $s @{ Version = '4.13.0'; Date = '2026-08-06' } | Out-Null
$afterText = [IO.File]::ReadAllText((Join-Path $s 'CHANGELOG.md'))
Add-Result 'Trailing hard break preserved' ($afterText -match 'Hard break\*\* first line\.  \r?\n') "two trailing spaces survive"
Remove-Item $s -Recurse -Force

# 9. -Check passes on the real repository, including when nothing is queued.
& (Join-Path $Repo 'scripts\assemble-changelog.ps1') -Check *>&1 | Out-Null
Add-Result '-Check passes on this repo' ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

# 10. Nothing above touched the repository's own changelog. Detected via the sentinel strings the sandboxes
#     inject - deliberately NOT by looking for the test's version number, which would start failing for real
#     the day the project legitimately ships that version.
$repoText = [IO.File]::ReadAllText((Join-Path $Repo 'CHANGELOG.md'))
$sentinels = @('Hand-written note', 'Sample fixed entry', 'Sample internal entry', 'Sample security entry', 'Hard break')
$hit = @($sentinels | Where-Object { $repoText -match [regex]::Escape($_) })
Add-Result 'Real repo changelog untouched by tests' ($hit.Count -eq 0) "sentinels found: $(if ($hit) { $hit -join ', ' } else { 'none' })"

$results | ForEach-Object { "{0}  {1,-42} {2}" -f $(if ($_.Ok) { 'PASS' } else { 'FAIL' }), $_.Test, $_.Detail }
$failed = @($results | Where-Object { -not $_.Ok }).Count
"`n$($results.Count - $failed)/$($results.Count) passed"
if ($failed -gt 0) { exit 1 }
exit 0

