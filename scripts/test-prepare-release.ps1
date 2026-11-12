#requires -Version 7.0
<#
.SYNOPSIS
    Dependency-free packaging regressions using isolated filesystem fixtures, never the real viewer.
#>
$ErrorActionPreference = 'Stop'
$prepare = Join-Path $PSScriptRoot 'prepare-release.ps1'
$verify = Join-Path $PSScriptRoot 'test-release-package.ps1'
$root = [System.IO.Directory]::CreateDirectory(
    (Join-Path ([System.IO.Path]::GetTempPath()) ("fujiy-package-tests-" + [guid]::NewGuid().ToString('N')))).FullName
$results = [System.Collections.Generic.List[object]]::new()
$required = @(
    'FujiyNotepad.WinUI.exe', 'FujiyNotepad.WinUI.pri', 'Assets\FujiyNotepad.ico',
    'Microsoft.Graphics.Canvas.dll', 'Microsoft.ui.xaml.dll', 'Microsoft.UI.Xaml.Internal.dll',
    'Microsoft.UI.Xaml.Controls.dll', 'Microsoft.UI.Xaml.Controls.pri', 'Microsoft.UI.pri',
    'Microsoft.WindowsAppRuntime.dll', 'Microsoft.WindowsAppRuntime.pri',
    'Microsoft.Windows.ApplicationModel.Resources.dll', 'MRM.dll',
    'en-US\Microsoft.ui.xaml.dll.mui'
)
$retained = $required + @(
    'resources.pri', 'Future.Runtime.dll', 'native\Future.Dependency.dll', 'DWriteCore.dll', 'dcompi.dll',
    'en-US\Microsoft.UI.Xaml.Phone.dll.mui', 'en-US\workloads.future.mui',
    'pt-BR\app-resources.pri', 'pt-BR\workloads.resources.pri', 'pt-BR\Microsoft.Windows.AI.localization.pri',
    'pt-BR\images\icon.png', 'de-DE\.app-resource',
    'Assets\preview.png', 'unknown [native].dll'
)
$removed = @(
    'FujiyNotepad.WinUI.pdb', 'native\debug.pdb', 'onnxruntime.dll', 'DirectML.dll',
    'Microsoft.Windows.AI.Search.Experimental.dll', 'Microsoft.Asg.SemanticIndex.AiFabric.Compatibility.dll',
    'Microsoft.Windows.Search.dll', 'Microsoft.Windows.Search.winmd', 'workloads.json',
    'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Windows.Workloads.Resources_ec.dll',
    'pt-BR\Microsoft.ui.xaml.dll.mui', 'fr-FR\Microsoft.ui.xaml.dll.mui', 'de-DE\framework.mui'
)

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Write-FixtureFile([string]$Path) {
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Path)) | Out-Null
    [System.IO.File]::WriteAllText($Path, "fixture: $([System.IO.Path]::GetFileName($Path))")
}
function Get-Snapshot([string]$Directory) {
    return (@(Get-ChildItem -LiteralPath $Directory -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $hash = if ($_.PSIsContainer) { 'directory' } else { (Get-FileHash -LiteralPath $_.FullName).Hash }
        '{0}|{1}|{2}' -f [System.IO.Path]::GetRelativePath($Directory, $_.FullName), $_.Attributes, $hash
    }) -join "`n")
}
function Expect-Failure([scriptblock]$Action, [string]$Message) {
    $errorText = ''
    try { & $Action | Out-Null } catch { $errorText = "$_" }
    Check ($errorText.Contains($Message)) "Expected '$Message', got '$errorText'."
}
function Invoke-Case([string]$Name, [scriptblock]$Test) {
    $directory = [System.IO.Directory]::CreateDirectory((Join-Path $root ([guid]::NewGuid().ToString('N')))).FullName
    $script:case = @{
        Directory = $directory
        Publish = Join-Path $directory 'publish [app]'
        Zip = Join-Path $directory 'dist\package [test].zip'
        Extract = Join-Path $directory 'extracted [app]'
        Diagnostics = Join-Path $directory 'diagnostics'
        UiCalls = 0
        UiExitCode = 0
        Junction = $null
    }
    foreach ($path in $retained + $removed) { Write-FixtureFile (Join-Path $case.Publish $path) }
    $hidden = Get-Item -LiteralPath (Join-Path $case.Publish 'de-DE\.app-resource') -Force
    $hidden.Attributes = $hidden.Attributes -bor [System.IO.FileAttributes]::Hidden
    # Header-only PE fixture, not an executable: enough to check RID validation without launching anything.
    $header = [byte[]]::new(72)
    $header[0] = 0x4D; $header[1] = 0x5A; $header[0x3C] = 0x40
    $header[0x40] = 0x50; $header[0x41] = 0x45; $header[0x44] = 0x64; $header[0x45] = 0x86
    [System.IO.File]::WriteAllBytes((Join-Path $case.Publish 'FujiyNotepad.WinUI.exe'), $header)
    try {
        & $Test
        $results.Add([pscustomobject]@{ Name = $Name; Passed = $true; Detail = '' })
    } catch {
        $results.Add([pscustomobject]@{ Name = $Name; Passed = $false; Detail = "$_" })
    } finally {
        if ($case.Junction) { Remove-Item -LiteralPath $case.Junction -Force }
    }
}

# The ZIP driver is exercised unchanged, but its only process boundary is faked.
function pwsh {
    param([switch]$NoProfile, [string]$File, [string]$Exe, [string]$ArtifactDir)
    Check ($NoProfile -and $File -eq (Join-Path $PSScriptRoot 'ui-tests.ps1')) 'Unexpected child command.'
    Check ($Exe -eq (Join-Path $case.Extract 'FujiyNotepad.WinUI.exe')) 'UI runner received the publish tree, not the extracted ZIP.'
    Check ($ArtifactDir -eq $case.Diagnostics) 'UI diagnostics escaped the fixture.'
    $case.UiCalls++
    $global:LASTEXITCODE = $case.UiExitCode
}
function Start-Process { throw 'Real process launches are forbidden in packaging tests.' }
function winapp { throw 'Real UI operations are forbidden in packaging tests.' }

try {
    foreach ($relative in $required) {
        foreach ($mode in 'missing', 'empty') {
            Invoke-Case "$mode required file fails before any mutation: $relative" {
                $path = Join-Path $case.Publish $relative
                if ($mode -eq 'missing') { [System.IO.File]::Delete($path) }
                else { [System.IO.File]::WriteAllBytes($path, [byte[]]::new(0)) }
                $before = Get-Snapshot $case.Publish
                Expect-Failure { & $prepare -PublishDir $case.Publish -ZipPath $case.Zip } $relative
                Check ((Get-Snapshot $case.Publish) -ceq $before) 'Invalid layout was mutated.'
                Check (-not (Test-Path -LiteralPath (Split-Path $case.Zip -Parent))) 'Invalid layout created packaging output.'
            }
        }
    }
    Invoke-Case 'Known unused files removed; unknown/native/localized files and every en-US MUI retained' {
        $hashes = @{}
        foreach ($relative in $retained) { $hashes[$relative] = (Get-FileHash -LiteralPath (Join-Path $case.Publish $relative)).Hash }
        $outside = Join-Path $case.Directory 'outside\do-not-delete.pdb'
        Write-FixtureFile $outside
        & $prepare -PublishDir $case.Publish
        foreach ($relative in $retained) {
            Check ((Get-FileHash -LiteralPath (Join-Path $case.Publish $relative)).Hash -eq $hashes[$relative]) "Changed retained file: $relative"
        }
        foreach ($relative in $removed) {
            Check (-not (Test-Path -LiteralPath (Join-Path $case.Publish $relative))) "Did not remove $relative"
        }
        Check (-not (Test-Path -LiteralPath (Join-Path $case.Publish 'fr-FR'))) 'Empty language directory remains.'
        Check (Test-Path -LiteralPath (Join-Path $case.Publish 'de-DE')) 'Directory containing a hidden app resource was removed.'
        Check (Test-Path -LiteralPath $outside) 'Pruning escaped the explicit staging directory.'
        $before = Get-Snapshot $case.Publish
        & $prepare -PublishDir $case.Publish
        Check ((Get-Snapshot $case.Publish) -ceq $before) 'Repeated pruning changed the package.'
    }
    Invoke-Case 'Validation-only checks leave even unused files untouched' {
        $before = Get-Snapshot $case.Publish
        & $prepare -PublishDir $case.Publish -ValidateOnly
        Check ((Get-Snapshot $case.Publish) -ceq $before) 'Validation-only pruned files.'
    }
    Invoke-Case 'Final ZIP round-trips every retained file, including hidden files and bracketed paths' {
        & $prepare -PublishDir $case.Publish -ZipPath $case.Zip
        Expand-Archive -LiteralPath $case.Zip -DestinationPath $case.Extract
        & $prepare -PublishDir $case.Extract -ValidateOnly
        $source = @(Get-ChildItem -LiteralPath $case.Publish -Recurse -File -Force)
        $extracted = @(Get-ChildItem -LiteralPath $case.Extract -Recurse -File -Force)
        Check ($source.Count -eq $extracted.Count) 'ZIP changed the file count.'
        foreach ($file in $source) {
            $destination = Join-Path $case.Extract ([System.IO.Path]::GetRelativePath($case.Publish, $file.FullName))
            Check ((Get-FileHash -LiteralPath $destination).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash) "ZIP changed $destination"
        }
    }
    Invoke-Case 'Existing archive or archive inside publish tree fails without pruning' {
        Write-FixtureFile $case.Zip
        $before = Get-Snapshot $case.Directory
        Expect-Failure { & $prepare -PublishDir $case.Publish -ZipPath $case.Zip } 'existing archive'
        Expect-Failure { & $prepare -PublishDir $case.Publish -ZipPath (Join-Path $case.Publish 'nested.zip') } 'outside PublishDir'
        Check ((Get-Snapshot $case.Directory) -ceq $before) 'Rejected archive path mutated files.'
    }
    foreach ($ancestorKind in 'junction', 'file') {
        $descendants = @('', 'new\nested')
        if ($ancestorKind -eq 'junction') { $descendants += 'Assets' }
        foreach ($relativeParent in $descendants) {
            Invoke-Case "ZIP destination $ancestorKind fails before mutation (descendants='$relativeParent')" {
                $parent = Split-Path $case.Zip -Parent
                if ($ancestorKind -eq 'junction') {
                    $case.Junction = $parent
                    New-Item -ItemType Junction -Path $parent -Target $case.Publish | Out-Null
                } else {
                    Write-FixtureFile $parent
                }
                $zip = if ($relativeParent) { Join-Path $parent (Join-Path $relativeParent 'package.zip') } else { $case.Zip }
                $before = Get-Snapshot $case.Directory
                $errorText = ''
                try { & $prepare -PublishDir $case.Publish -ZipPath $zip } catch { $errorText = "$_" }
                Check ((Get-Snapshot $case.Directory) -ceq $before) 'Invalid ZIP ancestor caused mutation before rejection.'
                Check ($errorText.Contains('ZIP destination ancestor')) "Expected explicit ancestor rejection, got '$errorText'."
                Check (-not (Test-Path -LiteralPath $zip)) 'Invalid ZIP destination created an archive.'
            }
        }
    }
    Invoke-Case 'Broad roots and non-directory inputs fail without mutation' {
        $before = Get-Snapshot $case.Directory
        Expect-Failure { & $prepare -PublishDir (Split-Path $PSScriptRoot -Parent) } 'repository root'
        Expect-Failure { & $prepare -PublishDir ([System.IO.Path]::GetPathRoot($case.Publish)) } 'filesystem root'
        Expect-Failure { & $prepare -PublishDir (Join-Path $case.Publish 'FujiyNotepad.WinUI.exe') } 'not a filesystem directory'
        Check ((Get-Snapshot $case.Directory) -ceq $before) 'Rejected root mutated files.'
    }
    foreach ($linkLocation in 'root', 'child', 'ancestor') {
        Invoke-Case "Reparse-point $linkLocation fails before pruning either side" {
            $target = [System.IO.Directory]::CreateDirectory((Join-Path $case.Directory 'outside')).FullName
            Write-FixtureFile (Join-Path $target 'do-not-delete.pdb')
            $publishBefore = Get-Snapshot $case.Publish
            $outsideBefore = Get-Snapshot $target
            $case.Junction = if ($linkLocation -eq 'child') { Join-Path $case.Publish 'link' } else { Join-Path $case.Directory 'link' }
            $linkTarget = if ($linkLocation -eq 'child') { $target } else { $case.Publish }
            New-Item -ItemType Junction -Path $case.Junction -Target $linkTarget | Out-Null
            $inputPath = switch ($linkLocation) {
                'root' { $case.Junction }
                'child' { $case.Publish }
                'ancestor' { Join-Path $case.Junction 'Assets' }
            }
            Expect-Failure { & $prepare -PublishDir $inputPath } 'reparse point'
            Remove-Item -LiteralPath $case.Junction -Force
            $case.Junction = $null
            Check ((Get-Snapshot $case.Publish) -ceq $publishBefore) 'Linked publish tree was mutated.'
            Check ((Get-Snapshot $target) -ceq $outsideBefore) 'Link target was mutated.'
        }
    }
    Invoke-Case 'Package driver validates and launches only the extracted final ZIP' {
        & $prepare -PublishDir $case.Publish -ZipPath $case.Zip
        & $verify -ZipPath $case.Zip -ExtractDir $case.Extract -Rid win-x64 -ArtifactDir $case.Diagnostics
        Check ($case.UiCalls -eq 1) 'Extracted package did not reach the UI runner.'
        $identity = Get-Content -LiteralPath (Join-Path $case.Diagnostics 'package.json') -Raw | ConvertFrom-Json
        Check ($identity.zipSha256 -eq (Get-FileHash -LiteralPath $case.Zip).Hash) 'Evidence identifies the wrong ZIP.'
        Check (Test-Path -LiteralPath (Join-Path $case.Diagnostics 'package-files.csv')) 'Missing extracted inventory.'
    }
    Invoke-Case 'Corrupt final ZIP resource fails before UI while retaining ZIP and diagnostics' {
        & $prepare -PublishDir $case.Publish -ZipPath $case.Zip
        $archive = [System.IO.Compression.ZipFile]::Open($case.Zip, [System.IO.Compression.ZipArchiveMode]::Update)
        try { $archive.GetEntry('FujiyNotepad.WinUI.pri').Delete() } finally { $archive.Dispose() }
        $hash = (Get-FileHash -LiteralPath $case.Zip).Hash
        Expect-Failure { & $verify -ZipPath $case.Zip -ExtractDir $case.Extract -Rid win-x64 -ArtifactDir $case.Diagnostics } 'FujiyNotepad.WinUI.pri'
        Check ($case.UiCalls -eq 0) 'Invalid ZIP reached the UI runner.'
        Check ((Get-FileHash -LiteralPath $case.Zip).Hash -eq $hash) 'Failed validation changed the ZIP.'
        Check (Test-Path -LiteralPath (Join-Path $case.Diagnostics 'package-validation.log')) 'Failure transcript was lost.'
    }
    Invoke-Case 'Wrong packaged architecture fails before UI' {
        & $prepare -PublishDir $case.Publish -ZipPath $case.Zip
        Expect-Failure { & $verify -ZipPath $case.Zip -ExtractDir $case.Extract -Rid win-x86 -ArtifactDir $case.Diagnostics } 'architecture mismatch'
        Check ($case.UiCalls -eq 0) 'Wrong architecture reached the UI runner.'
    }
    Invoke-Case 'UI failure propagates without discarding the built ZIP or diagnostics' {
        $case.UiExitCode = 7
        & $prepare -PublishDir $case.Publish -ZipPath $case.Zip
        $hash = (Get-FileHash -LiteralPath $case.Zip).Hash
        Expect-Failure { & $verify -ZipPath $case.Zip -ExtractDir $case.Extract -Rid win-x64 -ArtifactDir $case.Diagnostics } 'exit code 7'
        Check ($case.UiCalls -eq 1) 'Expected UI failure was not exercised.'
        Check ((Get-FileHash -LiteralPath $case.Zip).Hash -eq $hash) 'UI failure changed the ZIP.'
        Check (Test-Path -LiteralPath (Join-Path $case.Diagnostics 'package-files.csv')) 'Failure lost package evidence.'
    }
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
$results | ForEach-Object { '{0} {1} {2}' -f $(if ($_.Passed) { 'PASS' } else { 'FAIL' }), $_.Name, $_.Detail }
$failed = @($results | Where-Object { -not $_.Passed }).Count
Write-Host "$($results.Count - $failed)/$($results.Count) packaging tests passed."
if ($failed) { exit 1 }
exit 0
