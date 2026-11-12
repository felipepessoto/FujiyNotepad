#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-x86', 'win-arm64')]
    [string]$Rid,
    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($env:GITHUB_ACTIONS -ne 'true' -or
    $env:GITHUB_REPOSITORY -ne 'felipepessoto/FujiyNotepad' -or
    $env:GITHUB_REF -ne 'refs/heads/felipepessoto-release-zip-validation') {
    throw 'This one-off validation may run only on its temporary GitHub Actions branch, never on a local desktop.'
}

$repository = 'felipepessoto/FujiyNotepad'
$sourceCommit = '7e7bb7e698932b0174bfc359ff99ccee3c479bf6'
$tagObject = '3558b7437c16bc7544b5b1aa00de27582240541e'
$packages = @{
    'win-x64' = @{
        Id = 738156402; Bytes = 27194302
        Hash = '87eaf0df685c87da14fe056536a3d0790ac03afd8c79445af229e890fc339f1f'
        HostArchitecture = 'X64'
    }
    'win-x86' = @{
        Id = 738156399; Bytes = 24686505
        Hash = 'dd8c22e389b90f5738d5846bc824fb74d7adf43ca70c92f5c7f4f319353cfb50'
        HostArchitecture = 'X64'
    }
    'win-arm64' = @{
        Id = 738156398; Bytes = 27045828
        Hash = '299480ac88d917626caf854c3aaeee5843845d1a2048717c02ddd9a219647a94'
        HostArchitecture = 'Arm64'
    }
}
$package = $packages[$Rid]
$assetName = "FujiyNotepad-4.16.0-$Rid.zip"
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Refusing to reuse validation outputs: $OutputDirectory"
}
$evidence = Join-Path $OutputDirectory 'evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
Start-Transcript -Path (Join-Path $evidence 'validation-transcript.txt') | Out-Null
$result = [ordered]@{
    outcome = 'failed'
    startedUtc = [DateTime]::UtcNow.ToString('o')
    sourceTag = 'v4.16.0'
    sourceCommit = $sourceCommit
    tagObject = $tagObject
    validationCommit = $env:GITHUB_SHA
    validationRef = $env:GITHUB_REF
    workflowRun = "$env:GITHUB_SERVER_URL/$repository/actions/runs/$env:GITHUB_RUN_ID"
    workflowAttempt = $env:GITHUB_RUN_ATTEMPT
    rid = $Rid
    asset = $assetName
    assetId = $package.Id
    expectedBytes = $package.Bytes
    expectedSha256 = $package.Hash
    releaseIdentityVerified = $false
    zipVerified = $false
    uiExitCode = $null
}
$exitCode = 1

function Read-GitHubJson([string]$Endpoint, [string]$FileName) {
    Write-Host "gh api $Endpoint"
    $json = & gh api $Endpoint
    if ($LASTEXITCODE -ne 0) { throw "GitHub API failed: $Endpoint (exit $LASTEXITCODE)" }
    $json | Set-Content -LiteralPath (Join-Path $evidence $FileName) -Encoding utf8
    return ($json | ConvertFrom-Json)
}

try {
    Write-Host ".\scripts\validate-release-zip.ps1 -Rid '$Rid' -OutputDirectory '$OutputDirectory'"
    $head = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -ne $env:GITHUB_SHA) { throw 'Unexpected validation checkout.' }
    $parent = & git rev-parse 'HEAD^'
    if ($LASTEXITCODE -ne 0 -or $parent -ne $sourceCommit) { throw 'Validation must be a single commit on the authorized source.' }
    $changed = @(& git diff --name-only $sourceCommit HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the validation diff.' }
    $allowed = @('.github/workflows/release.yml', 'scripts/ui-tests.ps1', 'scripts/validate-release-zip.ps1')
    if (@($changed | Where-Object { $_ -notin $allowed }).Count -gt 0) {
        throw "Unexpected source changes: $($changed -join ', ')"
    }
    & git --no-pager diff $sourceCommit HEAD -- .github\workflows\release.yml scripts\ui-tests.ps1 scripts\validate-release-zip.ps1 |
        Set-Content -LiteralPath (Join-Path $evidence 'validation-only.patch') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Could not preserve the validation diff.' }
    Copy-Item -LiteralPath $PSCommandPath, '.\scripts\ui-tests.ps1', '.\.github\workflows\release.yml' -Destination $evidence

    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, Architecture, NumberOfCores, NumberOfLogicalProcessors)
    $cliVersion = (& winapp --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the installed winapp version.' }
    $environment = [ordered]@{
        runnerName = $env:RUNNER_NAME
        runnerOs = $env:RUNNER_OS
        runnerArchitecture = $env:RUNNER_ARCH
        imageOs = $env:ImageOS
        imageVersion = $env:ImageVersion
        osCaption = $os.Caption
        osVersion = $os.Version
        osBuild = $os.BuildNumber
        osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        is64BitOperatingSystem = [Environment]::Is64BitOperatingSystem
        powershellArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        powershellVersion = $PSVersionTable.PSVersion.ToString()
        cpu = $cpu
        winappVersion = $cliVersion
        culture = (Get-Culture).Name
        uiCulture = (Get-UICulture).Name
    }
    $environment | ConvertTo-Json -Depth 6 | Tee-Object -FilePath (Join-Path $evidence 'environment.json') | Write-Host
    if ($environment.osArchitecture -ne $package.HostArchitecture -or
        $environment.runnerArchitecture -ne $package.HostArchitecture) {
        throw "Unexpected host architecture for $Rid."
    }
    Write-Host 'Hosted images contain development tools/runtimes; this is not clean-runtime-machine evidence.'

    $tagRef = Read-GitHubJson "repos/$repository/git/ref/tags/v4.16.0" 'tag-ref.json'
    if ($tagRef.object.type -ne 'tag' -or $tagRef.object.sha -ne $tagObject) { throw 'Release tag identity changed.' }
    $tag = Read-GitHubJson "repos/$repository/git/tags/$tagObject" 'tag.json'
    if ($tag.tag -ne 'v4.16.0' -or $tag.object.type -ne 'commit' -or $tag.object.sha -ne $sourceCommit) {
        throw 'Release source commit changed.'
    }
    $release = Read-GitHubJson "repos/$repository/releases/tags/v4.16.0" 'release.json'
    if ($release.id -ne 442519852 -or $release.tag_name -ne 'v4.16.0' -or $release.draft -or $release.prerelease) {
        throw 'Unexpected release identity/state.'
    }
    foreach ($entry in $packages.GetEnumerator()) {
        $assets = @($release.assets | Where-Object { $_.name -eq "FujiyNotepad-4.16.0-$($entry.Key).zip" })
        if ($assets.Count -ne 1 -or $assets[0].id -ne $entry.Value.Id -or
            $assets[0].size -ne $entry.Value.Bytes -or $assets[0].digest -ne "sha256:$($entry.Value.Hash)") {
            throw "Release asset metadata changed for $($entry.Key)."
        }
    }
    $result.releaseIdentityVerified = $true

    Write-Host "gh release download v4.16.0 --repo $repository --pattern '$assetName' --dir '$OutputDirectory'"
    & gh release download v4.16.0 --repo $repository --pattern $assetName --dir $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Release ZIP download failed (exit $LASTEXITCODE)." }
    $zip = Join-Path $OutputDirectory $assetName
    Write-Host "Get-FileHash -LiteralPath '$zip' -Algorithm SHA256"
    $result.actualBytes = (Get-Item -LiteralPath $zip).Length
    $result.actualSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($result.actualBytes -ne $package.Bytes -or $result.actualSha256 -ne $package.Hash) {
        throw 'Downloaded ZIP does not match the authorized bytes/hash; refusing extraction or execution.'
    }
    $result.zipVerified = $true
    Write-Host "Verified $assetName : $($result.actualBytes) bytes; SHA256 $($result.actualSha256)"

    $appDirectory = Join-Path $OutputDirectory 'app'
    Write-Host "Expand-Archive -LiteralPath '$zip' -DestinationPath '$appDirectory'"
    Expand-Archive -LiteralPath $zip -DestinationPath $appDirectory
    $result.extractedFiles = @(Get-ChildItem -LiteralPath $appDirectory -Recurse -File).Count
    $exe = Join-Path $appDirectory 'FujiyNotepad.WinUI.exe'
    $version = (Get-Item -LiteralPath $exe).VersionInfo
    $result.executable = $exe
    $result.executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    $result.fileVersion = $version.FileVersion
    $result.productVersion = $version.ProductVersion
    if ($version.FileVersion -ne '4.16.0.0' -or $version.ProductVersion -ne "4.16.0+$sourceCommit") {
        throw 'Unexpected executable version/source identity.'
    }

    $architecture = $Rid.Substring(4)
    Write-Host "pwsh -NoLogo -NoProfile -File .\scripts\ui-tests.ps1 -Exe '$exe' -ArtifactDir '$evidence' -ExpectedArchitecture '$architecture'"
    & pwsh -NoLogo -NoProfile -File .\scripts\ui-tests.ps1 -Exe $exe -ArtifactDir $evidence -ExpectedArchitecture $architecture
    $result.uiExitCode = $LASTEXITCODE
    $result.executableUnchanged = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant() -eq $result.executableSha256
    if ($result.uiExitCode -ne 0) { throw "Release ZIP UI sweep failed (exit $($result.uiExitCode)); no retry." }
    if (-not $result.executableUnchanged) { throw 'The tested executable changed during validation.' }
    $ui = Get-Content -LiteralPath (Join-Path $evidence 'ui-results.json') -Raw | ConvertFrom-Json
    if ($ui.failures -ne 0 -or -not $ui.cleanupSucceeded) { throw 'UI result/cleanup did not succeed.' }
    $result.outcome = 'passed'
    $exitCode = 0
}
catch {
    $result.error = ($_ | Out-String).Trim()
    Write-Host "VALIDATION FAILED: $($result.error)" -ForegroundColor Red
}
finally {
    $crashLog = Join-Path $env:LOCALAPPDATA 'FujiyNotepad\crash.log'
    if (Test-Path -LiteralPath $crashLog) {
        Copy-Item -LiteralPath $crashLog -Destination (Join-Path $evidence 'crash.log')
    }
    $result.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
    @(
        "## $Rid : $($result.outcome)"
        "- Source: v4.16.0 / $sourceCommit"
        "- Asset: $assetName"
        "- ZIP identity: $($result.zipVerified); SHA256: $($result.actualSha256)"
        "- UI exit code: $($result.uiExitCode)"
        '- Exact commands, host/process architecture, assertions, and any failure screenshot are in the evidence artifact.'
        '- Not evidence for Windows 10, a 32-bit OS, minimum CPU hardware, runtime-free machines, or physical-device interaction.'
    ) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    Stop-Transcript | Out-Null
}
exit $exitCode
