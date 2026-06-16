#requires -Version 7.0
<#
.SYNOPSIS
    Summarize and gate code coverage for the FujiyNotepad libraries.

.DESCRIPTION
    Reads the cobertura XML files produced by `dotnet test --collect:"XPlat Code Coverage"` and:
      1. prints a per-library line / branch coverage table (and writes it to the GitHub Actions run
         summary via $GITHUB_STEP_SUMMARY, so the percentage is visible in the CI UI without downloading
         the artifact), and
      2. fails (exit 1) if any gated library's LINE coverage is below -Threshold.

    Only the two production libraries are gated. The WinUI app is intentionally excluded - it is covered by
    the separate `UI smoke` job, not unit coverage - and FujiyNotepad.TestSupport is test infrastructure
    (already marked [ExcludeFromCodeCoverage]).

    Each library is measured by its own test project; the other project only incidentally touches Core, so we
    take the highest (most complete) line/branch rate seen per package across all cobertura files.

.PARAMETER ResultsDir
    Directory containing the `**/coverage.cobertura.xml` files (the --results-directory passed to dotnet test).

.PARAMETER Threshold
    Minimum acceptable LINE coverage percentage for each gated library. Default 85. This is a floor (a safety
    net against silent erosion), not a target - the libraries currently sit well above it.

.PARAMETER Packages
    The cobertura package names to summarize and gate. Defaults to the two production libraries.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ResultsDir,
    [double]$Threshold = 85,
    [string[]]$Packages = @('FujiyNotepad.Core', 'FujiyNotepad.Presentation')
)

$ErrorActionPreference = 'Stop'

$files = Get-ChildItem -Path $ResultsDir -Recurse -Filter 'coverage.cobertura.xml' -ErrorAction SilentlyContinue
if (-not $files) {
    Write-Host "::error::No coverage.cobertura.xml found under '$ResultsDir'. Did 'dotnet test --collect' run?"
    exit 1
}

# package name -> @{ Line = best line %, Branch = best branch % } (highest = most complete measurement).
$best = @{}
foreach ($f in $files) {
    [xml]$xml = Get-Content $f.FullName
    foreach ($pkg in $xml.coverage.packages.package) {
        $line = [double]$pkg.'line-rate' * 100
        $branch = [double]$pkg.'branch-rate' * 100
        if (-not $best.ContainsKey($pkg.name) -or $line -gt $best[$pkg.name].Line) {
            $best[$pkg.name] = @{ Line = $line; Branch = $branch }
        }
    }
}

$rows = New-Object System.Collections.Generic.List[string]
$rows.Add('| Library | Line | Branch | Status |')
$rows.Add('| --- | ---: | ---: | :---: |')

$failures = 0
foreach ($name in $Packages) {
    if (-not $best.ContainsKey($name)) {
        $rows.Add(("| {0} | n/a | n/a | MISSING |" -f $name))
        Write-Host "::error::No coverage measured for '$name'."
        $failures++
        continue
    }
    $line = $best[$name].Line
    $branch = $best[$name].Branch
    $ok = $line -ge $Threshold
    if (-not $ok) {
        Write-Host ("::error::{0} line coverage {1:N1}% is below the {2:N0}% threshold." -f $name, $line, $Threshold)
        $failures++
    }
    $rows.Add(("| {0} | {1:N1}% | {2:N1}% | {3} |" -f $name, $line, $branch, $(if ($ok) { 'PASS' } else { 'FAIL' })))
}

$summary = @()
$summary += "## Code coverage (line gate: $([math]::Round($Threshold))%)"
$summary += ''
$summary += $rows
$summary += ''
$summary += "_Production libraries only. The WinUI app is covered by the UI smoke job; TestSupport is excluded._"
$summaryText = $summary -join "`n"

Write-Host $summaryText
if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $summaryText
}

if ($failures -gt 0) {
    Write-Host "`nCoverage gate FAILED ($failures issue(s))."
    exit 1
}
Write-Host "`nCoverage gate passed."
exit 0
