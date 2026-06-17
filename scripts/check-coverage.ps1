#requires -Version 7.0
<#
.SYNOPSIS
    Gate code coverage for the FujiyNotepad libraries.

.DESCRIPTION
    Reads a single (merged) Cobertura file - produced by ReportGenerator, which merges the per-test-project
    coverage and scopes it to the production libraries via assemblyfilters - and fails (exit 1) if any gated
    package's LINE coverage is below -Threshold.

    The human-readable coverage summary itself is produced by ReportGenerator (MarkdownSummaryGithub) and
    shown in the CI run summary; this script is only the pass/fail gate. Line-only on purpose (branch coverage
    is noisier). The threshold is a floor against silent erosion, not a target.

.PARAMETER CoberturaFile
    Path to the merged Cobertura XML (e.g. coveragereport/Cobertura.xml from ReportGenerator).

.PARAMETER Threshold
    Minimum acceptable LINE coverage percentage for each gated library. Default 85.

.PARAMETER Packages
    The cobertura package names to gate. Defaults to the two production libraries.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CoberturaFile,
    [double]$Threshold = 85,
    [string[]]$Packages = @('FujiyNotepad.Core', 'FujiyNotepad.Presentation')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $CoberturaFile)) {
    Write-Host "::error::Coverage file not found: '$CoberturaFile'. Did the ReportGenerator step run?"
    exit 1
}

[xml]$xml = Get-Content $CoberturaFile
$rates = @{}
foreach ($pkg in $xml.coverage.packages.package) {
    $rates[$pkg.name] = [double]$pkg.'line-rate' * 100
}

$failures = 0
foreach ($name in $Packages) {
    if (-not $rates.ContainsKey($name)) {
        Write-Host "::error::No coverage measured for '$name'."
        $failures++
        continue
    }
    $line = $rates[$name]
    if ($line -ge $Threshold) {
        Write-Host ("  PASS  {0,-32} line {1:N1}% (>= {2:N0}%)" -f $name, $line, $Threshold)
    }
    else {
        Write-Host ("::error::{0} line coverage {1:N1}% is below the {2:N0}% threshold." -f $name, $line, $Threshold)
        $failures++
    }
}

if ($failures -gt 0) {
    Write-Host "`nCoverage gate FAILED ($failures issue(s))."
    exit 1
}
Write-Host "`nCoverage gate passed (line floor ${Threshold}%)."
exit 0
