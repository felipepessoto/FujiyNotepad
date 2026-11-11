#requires -Version 7.0
<#
.SYNOPSIS
    Prunes a self-contained publish using the same policy in CI and releases.
.PARAMETER PublishDir
    The published app directory. Required runtime/resources are checked before any files are removed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDir
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath (Resolve-Path -LiteralPath $PublishDir).Path)
if (-not $root.PSIsContainer) {
    throw "Publish directory is not a directory: $PublishDir"
}

$required = @(
    'FujiyNotepad.WinUI.exe', 'FujiyNotepad.WinUI.pri', 'Assets\FujiyNotepad.ico',
    'Microsoft.Graphics.Canvas.dll', 'Microsoft.ui.xaml.dll', 'Microsoft.UI.Xaml.Internal.dll',
    'Microsoft.UI.Xaml.Controls.dll', 'Microsoft.UI.Xaml.Controls.pri', 'Microsoft.UI.pri',
    'Microsoft.WindowsAppRuntime.dll', 'Microsoft.WindowsAppRuntime.pri',
    'Microsoft.Windows.ApplicationModel.Resources.dll', 'MRM.dll',
    'en-US\Microsoft.ui.xaml.dll.mui'
)
foreach ($relativePath in $required) {
    $path = Join-Path $root.FullName $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Required release file is missing or empty: $relativePath"
    }
}

# Only remove known unused components, not an allowlist of runtime DLLs: new SDK dependencies must survive.
# The viewer implements its own streaming search; the SDK's AI/AppContentIndex search is not used.
$unused = @(
    'onnxruntime.dll', 'DirectML.dll', 'Microsoft.ML.OnnxRuntime.dll',
    'Microsoft.Web.WebView2.Core.dll', 'WebView2Loader.dll',
    'Microsoft.Windows.Widgets.dll', 'PerceptiveStreaming.dll', 'NPUDetect.dll',
    'PushNotificationsLongRunningTask.ProxyStub.dll',
    'Microsoft.UI.Xaml.Phone.dll', 'Microsoft.UI.Designer.dll', 'Microsoft.Graphics.Imaging.dll',
    'Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.dll',
    'Microsoft.Windows.Workloads.dll', 'Microsoft.Windows.Workloads.Resources.dll', 'Microsoft.Windows.Workloads.Resources_ec.dll',
    'Microsoft.Asg.SemanticIndex.AiFabric.Compatibility.dll', 'Microsoft.Windows.Search.dll', 'Microsoft.Windows.Search.winmd'
)
Get-ChildItem -LiteralPath $root.FullName -Recurse -File | Where-Object {
    $_.Extension -eq '.pdb' -or $_.Name -in $unused -or
    $_.Name -like 'Microsoft.Windows.AI.*' -or $_.Name -like 'workloads.*'
} | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

# en-US is WinUI's ultimate MUI fallback, independent of the app's en-US/pt-BR resources in its PRI.
# Remove only MUI files, not entire language directories that may also hold app resources.
foreach ($directory in Get-ChildItem -LiteralPath $root.FullName -Directory) {
    if ($directory.Name -eq 'en-US') { continue }
    $muiFiles = @(Get-ChildItem -LiteralPath $directory.FullName -Filter '*.mui' -File)
    if ($muiFiles.Count -eq 0) { continue }
    $muiFiles | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    if (@(Get-ChildItem -LiteralPath $directory.FullName -Force).Count -eq 0) {
        Remove-Item -LiteralPath $directory.FullName -Force
    }
}

$files = Get-ChildItem -LiteralPath $root.FullName -Recurse -File
Write-Host "Release layout ready: $($files.Count) files, $(($files | Measure-Object Length -Sum).Sum) bytes."
