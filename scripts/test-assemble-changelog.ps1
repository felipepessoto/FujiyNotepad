<#
.SYNOPSIS
    Self-test for scripts/assemble-changelog.ps1.

.DESCRIPTION
    The assemble script rewrites CHANGELOG.md, so it is worth checking rather than trusting. Each case runs
    against a throwaway copy of the repository's changelog and fragments.

    Every path here is ABSOLUTE on purpose. PowerShell's Push-Location does not change .NET's current
    directory, so a [System.IO.File] call with a relative path silently hits the real repository instead of
    the copy under test - which, while this was being written, quietly wrote test data into the real
    CHANGELOG.md five times before it was noticed. The last case asserts the repository's own changelog was
    left alone, so that failure mode cannot recur silently.
#>
param([string]$Repo = (Split-Path $PSScriptRoot -Parent))

# Verifies scripts/assemble-changelog.ps1 against throwaway copies.
# Every path here is absolute: PowerShell's Push-Location does NOT change .NET's current directory, so
# [IO.File] calls with a relative path silently hit the real repository instead of the copy under test.

$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$results = @()

function New-Sandbox {
    param([string]$Repo, [switch]$UseLf)
    $dir = Join-Path $env:TEMP ("clog-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path (Join-Path $dir 'scripts') -Force | Out-Null
    Copy-Item (Join-Path $Repo 'changelog.d') $dir -Recurse
    Copy-Item (Join-Path $Repo 'scripts\assemble-changelog.ps1') (Join-Path $dir 'scripts')

    $text = [IO.File]::ReadAllText((Join-Path $Repo 'CHANGELOG.md'))
    if ($UseLf) { $text = $text -replace "`r`n", "`n" }
    [IO.File]::WriteAllText((Join-Path $dir 'CHANGELOG.md'), $text, $utf8)
    return $dir
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

# 1. A CRLF changelog must stay CRLF, with no CR CR LF.
$s = New-Sandbox -Repo $Repo
& (Join-Path $s 'scripts\assemble-changelog.ps1') -Version 4.13.0 -Date 2026-08-06 *>&1 | Out-Null
$e = Get-Endings (Join-Path $s 'CHANGELOG.md')
Add-Result 'CRLF file stays CRLF' ($e.Lf -eq 0 -and $e.DoubleCr -eq 0) "CRLF=$($e.CrLf) LF=$($e.Lf) CRCR=$($e.DoubleCr)"
Remove-Item $s -Recurse -Force

# 2. An LF changelog must stay LF.
$s = New-Sandbox -Repo $Repo -UseLf
& (Join-Path $s 'scripts\assemble-changelog.ps1') -Version 4.13.0 -Date 2026-08-06 *>&1 | Out-Null
$e = Get-Endings (Join-Path $s 'CHANGELOG.md')
Add-Result 'LF file stays LF' ($e.CrLf -eq 0 -and $e.DoubleCr -eq 0) "CRLF=$($e.CrLf) LF=$($e.Lf) CRCR=$($e.DoubleCr)"
Remove-Item $s -Recurse -Force

# 3. Release consumes fragments and writes the section.
$s = New-Sandbox -Repo $Repo
$before = (Get-ChildItem (Join-Path $s 'changelog.d') -Recurse -File -Filter *.md | Where-Object Name -ne 'README.md').Count
& (Join-Path $s 'scripts\assemble-changelog.ps1') -Version 4.13.0 -Date 2026-08-06 *>&1 | Out-Null
$after = (Get-ChildItem (Join-Path $s 'changelog.d') -Recurse -File -Filter *.md | Where-Object Name -ne 'README.md').Count
$text = [IO.File]::ReadAllText((Join-Path $s 'CHANGELOG.md'))
$ok = ($after -eq 0) -and ($text -match '## \[4\.13\.0\] - 2026-08-06') -and ($text -match 'changelog\.d/<category>/')
Add-Result 'Release consumes fragments' $ok "fragments $before -> $after; section written; [Unreleased] pointer kept"
Remove-Item $s -Recurse -Force

# 4. No stray warning when [Unreleased] holds only the pointer.
$s = New-Sandbox -Repo $Repo
$warn = & (Join-Path $s 'scripts\assemble-changelog.ps1') -Version 4.13.0 -Date 2026-08-06 3>&1 2>$null |
    Where-Object { $_ -is [System.Management.Automation.WarningRecord] }
Add-Result 'No false stray-bullet warning' ($null -eq $warn) "warnings: $(($warn | ForEach-Object { $_.Message }) -join '; ')"
Remove-Item $s -Recurse -Force

# 5. A hand-written inline bullet is carried into the release, not dropped.
$s = New-Sandbox -Repo $Repo
$p = Join-Path $s 'CHANGELOG.md'
$t = [IO.File]::ReadAllText($p) -replace '(?s)(## \[Unreleased\])', "`$1`r`n`r`n### Added`r`n- **Hand-written note** left here out of habit (issue #999)."
[IO.File]::WriteAllText($p, $t, $utf8)
$injected = ([IO.File]::ReadAllText($p)) -match 'Hand-written note'
& (Join-Path $s 'scripts\assemble-changelog.ps1') -Version 4.13.0 -Date 2026-08-06 *>&1 | Out-Null
$carried = ([IO.File]::ReadAllText($p)) -match 'Hand-written note'
Add-Result 'Inline bullet carried, not dropped' ($injected -and $carried) "injected=$injected carried=$carried"
Remove-Item $s -Recurse -Force

# 6. Malformed fragments are rejected and the changelog is left alone.
$s = New-Sandbox -Repo $Repo
[IO.File]::WriteAllText((Join-Path $s 'changelog.d\stray.md'), "- misplaced", $utf8)
New-Item -ItemType Directory -Path (Join-Path $s 'changelog.d\bogus') | Out-Null
[IO.File]::WriteAllText((Join-Path $s 'changelog.d\bogus\1-x.md'), "- wrong category", $utf8)
[IO.File]::WriteAllText((Join-Path $s 'changelog.d\fixed\900-empty.md'), "", $utf8)
[IO.File]::WriteAllText((Join-Path $s 'changelog.d\fixed\901-prose.md'), "no bullet here", $utf8)
$snapshot = [IO.File]::ReadAllText((Join-Path $s 'CHANGELOG.md'))
& (Join-Path $s 'scripts\assemble-changelog.ps1') -Check *>&1 | Out-Null
$checkExit = $LASTEXITCODE
& (Join-Path $s 'scripts\assemble-changelog.ps1') -Version 4.13.0 *>&1 | Out-Null
$relExit = $LASTEXITCODE
$untouched = ([IO.File]::ReadAllText((Join-Path $s 'CHANGELOG.md')) -eq $snapshot)
Add-Result 'Malformed rejected, changelog untouched' (($checkExit -ne 0) -and ($relExit -ne 0) -and $untouched) "check exit=$checkExit release exit=$relExit untouched=$untouched"
Remove-Item $s -Recurse -Force

# 7. -Check passes on the real repository.
& (Join-Path $Repo 'scripts\assemble-changelog.ps1') -Check *>&1 | Out-Null
Add-Result '-Check passes on this repo' ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

# 8. The repository's own changelog was not touched by any of the above.
$repoText = [IO.File]::ReadAllText((Join-Path $Repo 'CHANGELOG.md'))
$clean = ($repoText -notmatch 'Hand-written note') -and ($repoText -notmatch 'hand-written entry') -and ($repoText -notmatch '4\.13\.0')
Add-Result 'Real repo changelog untouched by tests' $clean "no test strings, no 4.13.0 section"

$results | ForEach-Object { "{0}  {1,-42} {2}" -f $(if ($_.Ok) { 'PASS' } else { 'FAIL' }), $_.Test, $_.Detail }
$failed = @($results | Where-Object { -not $_.Ok }).Count
"`n$($results.Count - $failed)/$($results.Count) passed"
if ($failed -gt 0) { exit 1 }

