#requires -Version 7.0
<#
.SYNOPSIS
    Extracts the final ZIP, validates its resources/architecture, and runs the existing UI sweep.
.DESCRIPTION
    Run on a disposable Windows desktop/profile, normally a hosted runner. Never tests the publish tree.
    Keeps the ZIP, extracted files and diagnostics on failure; does not delete or overwrite old evidence.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ZipPath,
    [Parameter(Mandatory)]
    [string]$ExtractDir,
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-x86', 'win-arm64')]
    [string]$Rid,
    [Parameter(Mandatory)]
    [string]$ArtifactDir
)

$ErrorActionPreference = 'Stop'
$artifactPath = [System.IO.Path]::GetFullPath($ArtifactDir, (Get-Location).ProviderPath)
[System.IO.Directory]::CreateDirectory($artifactPath) | Out-Null
Start-Transcript -LiteralPath (Join-Path $artifactPath 'package-validation.log') -NoClobber | Out-Null
try {
    $zip = (Resolve-Path -LiteralPath $ZipPath).ProviderPath
    $extract = [System.IO.Path]::GetFullPath($ExtractDir, (Get-Location).ProviderPath)
    if (Test-Path -LiteralPath $extract) { throw "Extraction directory must not already exist: '$extract'." }

    $identity = [ordered]@{
        sourceRevision = $env:GITHUB_SHA
        runId = $env:GITHUB_RUN_ID
        rid = $Rid
        hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        zip = $zip
        zipBytes = (Get-Item -LiteralPath $zip).Length
        zipSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
        extractedDirectory = $extract
    }
    $identity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactPath 'package.json')
    Expand-Archive -LiteralPath $zip -DestinationPath $extract
    Get-ChildItem -LiteralPath $extract -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            path = [System.IO.Path]::GetRelativePath($extract, $_.FullName)
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    } | Export-Csv -LiteralPath (Join-Path $artifactPath 'package-files.csv') -NoTypeInformation
    & (Join-Path $PSScriptRoot 'prepare-release.ps1') -PublishDir $extract -ValidateOnly

    $exe = Join-Path $extract 'FujiyNotepad.WinUI.exe'
    $reader = [System.IO.BinaryReader]::new([System.IO.File]::OpenRead($exe))
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Packaged executable has no MZ header.' }
        $reader.BaseStream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $reader.BaseStream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Packaged executable has no PE header.' }
        $machine = $reader.ReadUInt16()
    } finally { $reader.Dispose() }
    $expectedMachine = @{ 'win-x64' = 0x8664; 'win-x86' = 0x014C; 'win-arm64' = 0xAA64 }[$Rid]
    if ($machine -ne $expectedMachine) {
        throw ("Packaged executable architecture mismatch for {0}: 0x{1:X4}." -f $Rid, $machine)
    }
    if ($Rid -eq 'win-arm64' -and $identity.hostArchitecture -ne 'Arm64') {
        throw 'ARM64 packages must be exercised on a native ARM64 host.'
    }
    Write-Host ("Validated {0} PE machine 0x{1:X4} on {2}; launching '{3}'." -f $Rid, $machine, $identity.hostArchitecture, $exe)

    # A child PowerShell keeps the harness's exit code from skipping this driver's finally block.
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'ui-tests.ps1') -Exe $exe -ArtifactDir $artifactPath
    if ($LASTEXITCODE -ne 0) { throw "Packaged UI tests failed with exit code $LASTEXITCODE." }
} finally {
    Stop-Transcript | Out-Null
}
