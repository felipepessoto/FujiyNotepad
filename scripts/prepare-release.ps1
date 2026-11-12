#requires -Version 7.0
<#
.SYNOPSIS
    Validates and conservatively prunes a self-contained publish, optionally creating its final ZIP.
.PARAMETER PublishDir
    Explicit staging directory. Required files and path safety are checked before any mutation.
.PARAMETER ZipPath
    New ZIP outside PublishDir. Existing archives are never overwritten.
.PARAMETER ValidateOnly
    Checks an extracted package without changing any files.
#>
[CmdletBinding(DefaultParameterSetName = 'Prepare')]
param(
    [Parameter(Mandatory)]
    [string]$PublishDir,
    [Parameter(ParameterSetName = 'Prepare')]
    [string]$ZipPath,
    [Parameter(Mandatory, ParameterSetName = 'Validate')]
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$root = Get-Item -LiteralPath (Resolve-Path -LiteralPath $PublishDir).ProviderPath -Force
if ($root -isnot [System.IO.DirectoryInfo]) {
    throw "Publish directory is not a filesystem directory: '$PublishDir'."
}
$separator = [System.IO.Path]::DirectorySeparatorChar
$prefix = $root.FullName.TrimEnd($separator) + $separator
$repository = (Split-Path $PSScriptRoot -Parent) + $separator
if ($null -eq $root.Parent -or $repository.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing a filesystem root, repository root or its ancestor as PublishDir: '$($root.FullName)'."
}

# Refuse links rather than following a junction/symlink outside the explicitly selected staging tree.
for ($ancestor = $root; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "Publish path contains a reparse point: '$($ancestor.FullName)'."
    }
}
$entries = @(Get-ChildItem -LiteralPath $root.FullName -Recurse -Force)
foreach ($entry in $entries) {
    if (-not $entry.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        ($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw "Publish entry is outside the staging tree or is a reparse point: '$($entry.FullName)'."
    }
}

# Minimum runtime/resource contract verified against the current self-contained outputs on all RIDs.
# This is not a file allowlist: everything not explicitly pruned below remains in the package.
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
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path -Force).Length -eq 0) {
        throw "Required release file is missing or empty: '$relativePath' in '$($root.FullName)'."
    }
}
if ($PSCmdlet.ParameterSetName -eq 'Validate') {
    Write-Host "Required release files validated: '$($root.FullName)'."
    return
}

if ($ZipPath) {
    $zip = [System.IO.Path]::GetFullPath($ZipPath, (Get-Location).ProviderPath)
    if ($zip.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $zip -eq $root.FullName) {
        throw "ZIP must be outside PublishDir: '$zip'."
    }
    if (Test-Path -LiteralPath $zip) { throw "Refusing to overwrite an existing archive: '$zip'." }
}

# SDK components are at the publish root; do not match their names inside app resource directories.
# Keep the existing exclusions, including unused AI/AppContentIndex search (the viewer searches itself).
$unused = @(
    'onnxruntime.dll', 'DirectML.dll', 'Microsoft.ML.OnnxRuntime.dll',
    'Microsoft.Web.WebView2.Core.dll', 'WebView2Loader.dll',
    'Microsoft.Windows.Widgets.dll', 'PerceptiveStreaming.dll', 'NPUDetect.dll',
    'PushNotificationsLongRunningTask.ProxyStub.dll',
    'Microsoft.UI.Xaml.Phone.dll', 'Microsoft.UI.Designer.dll', 'Microsoft.Graphics.Imaging.dll',
    'Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.dll',
    'Microsoft.Asg.SemanticIndex.AiFabric.Compatibility.dll',
    'Microsoft.Windows.Search.dll', 'Microsoft.Windows.Search.winmd',
    'Microsoft.Windows.Workloads.dll', 'Microsoft.Windows.Workloads.Resources.dll', 'Microsoft.Windows.Workloads.Resources_ec.dll'
)
$entries | Where-Object {
    -not $_.PSIsContainer -and $_.Extension -ne '.mui' -and (
        $_.Extension -eq '.pdb' -or ($_.DirectoryName -eq $root.FullName -and (
            $_.Name -in $unused -or $_.Name -like 'Microsoft.Windows.AI.*' -or $_.Name -like 'workloads.*')))
} | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

# en-US is the framework's ultimate fallback, separate from app localization in FujiyNotepad.WinUI.pri.
# Only remove MUI files from other top-level language folders; app resources and subdirectories survive.
foreach ($directory in Get-ChildItem -LiteralPath $root.FullName -Directory -Force) {
    if ($directory.Name -eq 'en-US') { continue }
    $muiFiles = @(Get-ChildItem -LiteralPath $directory.FullName -File -Force -Filter '*.mui')
    if ($muiFiles.Count -eq 0) { continue }
    $muiFiles | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    if (@(Get-ChildItem -LiteralPath $directory.FullName -Force).Count -eq 0) {
        [System.IO.Directory]::Delete($directory.FullName, $false)
    }
}

$files = @(Get-ChildItem -LiteralPath $root.FullName -Recurse -File -Force)
Write-Host "Release layout ready: $($files.Count) files, $(($files | Measure-Object Length -Sum).Sum) bytes."
if ($ZipPath) {
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($zip)) | Out-Null
    # Unlike Compress-Archive, ZipFile also retains hidden files and treats brackets literally.
    [System.IO.Compression.ZipFile]::CreateFromDirectory($root.FullName, $zip)
    Write-Host "ZIP: '$zip', $((Get-Item -LiteralPath $zip).Length) bytes, SHA256 $((Get-FileHash -LiteralPath $zip).Hash)"
}
