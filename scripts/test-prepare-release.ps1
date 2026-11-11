#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$scratch = [IO.Directory]::CreateDirectory(
    (Join-Path ([IO.Path]::GetTempPath()) "fujiy-release-test-$([guid]::NewGuid().ToString('N'))")
).FullName

function Write-FixtureFile([string]$RelativePath) {
    $path = Join-Path $scratch $RelativePath
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, "fixture: $RelativePath")
}

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "[PASS] $Message"
}

function Assert-MissingFileFails([string]$RelativePath) {
    $path = Join-Path $scratch $RelativePath
    Remove-Item -LiteralPath $path -Force
    Write-FixtureFile 'do-not-delete-before-validation.pdb'
    $failure = $null
    try { & "$PSScriptRoot\prepare-release.ps1" -PublishDir $scratch }
    catch { $failure = $_.Exception.Message }
    Assert ($null -ne $failure -and $failure.Contains($RelativePath)) "Missing $RelativePath fails explicitly"
    Assert (Test-Path -LiteralPath "$scratch\do-not-delete-before-validation.pdb") 'Invalid layouts fail before pruning'
    Write-FixtureFile $RelativePath
}

try {
    $retained = @(
        'FujiyNotepad.WinUI.exe', 'FujiyNotepad.WinUI.pri', 'Assets\FujiyNotepad.ico',
        'Microsoft.Graphics.Canvas.dll', 'Microsoft.ui.xaml.dll', 'Microsoft.UI.Xaml.Internal.dll',
        'Microsoft.UI.Xaml.Controls.dll', 'Microsoft.UI.Xaml.Controls.pri', 'Microsoft.UI.pri',
        'Microsoft.WindowsAppRuntime.dll', 'Microsoft.WindowsAppRuntime.pri',
        'Microsoft.Windows.ApplicationModel.Resources.dll', 'MRM.dll',
        'en-US\Microsoft.ui.xaml.dll.mui',
        'pt-BR\app-resources.pri', 'Assets\preview.png', 'Future.Runtime.dll'
    )
    $removed = @(
        'FujiyNotepad.WinUI.pdb', 'Microsoft.Windows.AI.Search.Experimental.dll',
        'Microsoft.Asg.SemanticIndex.AiFabric.Compatibility.dll', 'Microsoft.Windows.Search.dll',
        'Microsoft.Windows.Search.winmd', 'onnxruntime.dll', 'workloads.json',
        'pt-BR\Microsoft.ui.xaml.dll.mui', 'fr-FR\Microsoft.ui.xaml.dll.mui'
    )
    foreach ($path in $retained + $removed) { Write-FixtureFile $path }
    $priHash = (Get-FileHash -LiteralPath "$scratch\FujiyNotepad.WinUI.pri").Hash

    & "$PSScriptRoot\prepare-release.ps1" -PublishDir $scratch
    foreach ($path in $retained) {
        Assert (Test-Path -LiteralPath (Join-Path $scratch $path) -PathType Leaf) "Retained $path"
    }
    foreach ($path in $removed) {
        Assert (-not (Test-Path -LiteralPath (Join-Path $scratch $path))) "Removed $path"
    }
    Assert ((Get-FileHash -LiteralPath "$scratch\FujiyNotepad.WinUI.pri").Hash -eq $priHash) 'App PRI is unchanged'
    Assert (-not (Test-Path -LiteralPath "$scratch\fr-FR")) 'Empty framework language directory is removed'
    & "$PSScriptRoot\prepare-release.ps1" -PublishDir $scratch
    Assert (Test-Path -LiteralPath "$scratch\pt-BR\app-resources.pri") 'Repeated pruning preserves app localization'

    Assert-MissingFileFails 'FujiyNotepad.WinUI.pri'
    Assert-MissingFileFails 'en-US\Microsoft.ui.xaml.dll.mui'
    Assert-MissingFileFails 'Microsoft.Graphics.Canvas.dll'
    Write-Host 'Release preparation tests PASSED'
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
