[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare', 'Micro', 'Disk')][string]$Phase,
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [string]$BaselineRevision = '98f6e9e99cd4379fd84be2ce2bb52d22552daa7c',
    [string]$CandidateRevision = '01de6f9886c504ab3e9bb6282aece53d1fa65b0f',
    [string]$DotNet10 = 'C:\Program Files\dotnet\dotnet.exe',
    [string]$DotNet11,
    [long]$WorkloadBytes = 2GB,
    [ValidateRange(1, 20)][int]$Rounds = 2,
    [ValidateRange(1, 100)][int]$DiskIterations = 3,
    [switch]$MeasurementApproved,
    [switch]$Resume,
    [ValidateSet('ascii', 'utf8', 'utf16le', 'utf32le')][string[]]$ExcludeCharacterCountFor = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
$benchmarkRelative = 'src\FujiyNotepad.Benchmarks'
$nativeRelative = "$benchmarkRelative\NativeWorkloads\NativeWorkloads.csproj"
$manifestPath = Join-Path $ArtifactRoot 'comparison.json'

function Save-Json($Value, [string]$Path) {
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Record-Command([string]$WorkingDirectory, [string]$Executable, [string[]]$Arguments) {
    [ordered]@{
        utc = [DateTimeOffset]::UtcNow.ToString('o')
        workingDirectory = $WorkingDirectory
        executable = $Executable
        arguments = $Arguments
    } | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $ArtifactRoot 'commands.jsonl') -Encoding utf8
}

function Invoke-Checked([string]$Executable, [string[]]$Arguments, [string]$Log) {
    Record-Command (Get-Location).Path $Executable $Arguments
    & $Executable @Arguments 2>&1 | Tee-Object -FilePath $Log
    if ($LASTEXITCODE -ne 0) { throw "$Executable exited with $LASTEXITCODE. See $Log" }
}

function Invoke-DotNet($Side, [string[]]$Arguments, [string]$Log) {
    $oldRoot = $env:DOTNET_ROOT
    $oldPath = $env:PATH
    try {
        $env:DOTNET_ROOT = Split-Path $Side.dotnet -Parent
        $env:PATH = "$env:DOTNET_ROOT;C:\Program Files (x86)\Microsoft Visual Studio\Installer;$oldPath"
        Push-Location $Side.source
        try {
            # Keep vcvars and the build in one process. Do not stop shared compiler/build servers.
            $command = 'call "' + $Side.vcvars + '" >nul && "' + $Side.dotnet + '" '
            $command += ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
            Invoke-Checked $env:ComSpec @('/d', '/s', '/c', $command) $Log
        }
        finally { Pop-Location }
    }
    finally {
        $env:DOTNET_ROOT = $oldRoot
        $env:PATH = $oldPath
    }
}

function Assert-MicroResults([string]$Root, $Side, [int]$Iterations) {
    $report = Get-Content -LiteralPath (Join-Path $Root 'results\FujiyNotepad.Benchmarks.EngineBenchmarks-report-full.json') `
        -Raw | ConvertFrom-Json
    $methods = @('IndexLines', 'SearchFindAll', 'FindForwardFirst', 'GetLineRandom', 'GetLineRandomCold', 'FilterDecode', 'FilterByteScan')
    if ($report.Benchmarks.Count -ne $methods.Count -or $report.HostEnvironmentInfo.DotNetCliVersion -ne $Side.sdk) {
        throw 'Unexpected benchmark count or SDK version.'
    }
    foreach ($method in $methods) {
        $benchmark = @($report.Benchmarks | Where-Object Method -eq $method)
        if ($benchmark.Count -ne 1 -or $benchmark[0].Statistics.N -ne $Iterations -or
            $benchmark[0].DisplayInfo -notlike '*Runtime=NativeAOT*') {
            throw "Missing Native AOT measurements or unexpected iteration count: $method"
        }
    }
}

function Assert-MeasurementSlot {
    if (Test-Path -LiteralPath (Join-Path $ArtifactRoot 'pause-measurements.json')) {
        throw 'Measurements paused by the coordinator. Completed results are preserved; obtain a new slot before removing pause-measurements.json.'
    }
}

function Preserve-Interrupted([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $destination = Join-Path $ArtifactRoot ("interrupted\" + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'))
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Move-Item -LiteralPath $Path -Destination $destination
        Write-Warning "Preserved interrupted output in $destination; the incomplete batch will be rerun."
    }
}

function Record-Host([string]$Path) {
    Save-Json ([ordered]@{
        utc = [DateTimeOffset]::UtcNow.ToString('o')
        os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, FreePhysicalMemory
        cpu = Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed
        machine = Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model, TotalPhysicalMemory
        disk = Get-PSDrive -Name ([IO.Path]::GetPathRoot($ArtifactRoot).Substring(0, 1)) | Select-Object Used, Free
        powerPlan = (& powercfg /getactivescheme) -join "`n"
        cpuSnapshot = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" |
            Select-Object PercentProcessorTime, PercentPrivilegedTime
        dotnetEnvironment = [ordered]@{
            DOTNET_GCServer = $env:DOTNET_GCServer
            DOTNET_GCHeapHardLimit = $env:DOTNET_GCHeapHardLimit
            COMPlus_GCServer = $env:COMPlus_GCServer
        }
    }) $Path
}

if ($Phase -eq 'Prepare') {
    if (Test-Path -LiteralPath $manifestPath) { throw 'Use a new artifact directory; an existing comparison must not be overwritten.' }
    if (!$DotNet11 -or !(Test-Path -LiteralPath $DotNet11) -or !(Test-Path -LiteralPath $DotNet10)) {
        throw 'Supply installed .NET 10 and .NET 11 dotnet.exe paths.'
    }
    New-Item -ItemType Directory -Path $ArtifactRoot -Force | Out-Null
    $vs = & 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe' -latest -products '*' `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$vs) { throw 'The x64 MSVC toolchain is required.' }
    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    $sides = @(
        [ordered]@{ name = 'net10'; revision = $BaselineRevision; dotnet = [IO.Path]::GetFullPath($DotNet10); sdk = '10.0.404'; runtime = '10.0.15' },
        [ordered]@{ name = 'net11'; revision = $CandidateRevision; dotnet = [IO.Path]::GetFullPath($DotNet11); sdk = '11.0.100'; runtime = '11.0.0' }
    )
    Record-Host (Join-Path $ArtifactRoot 'host-prepare.json')
    $harnessRoot = Join-Path $repo $benchmarkRelative
    $harnessFiles = Get-ChildItem -LiteralPath $harnessRoot -File -Recurse |
        Where-Object { $_.FullName.Substring($harnessRoot.Length) -notmatch '\\(bin|obj|BenchmarkDotNet.Artifacts|\.bdn)\\' }
    $hashes = @($harnessFiles | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($harnessRoot.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    foreach ($side in $sides) {
        $side.source = Join-Path $ArtifactRoot "sources\$($side.name)"
        $side.vcvars = $vcvars
        $side.native = Join-Path $ArtifactRoot "native\$($side.name)"
        $side.revision = (& git -C $repo rev-parse "$($side.revision)^{commit}").Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source revision.' }
        $archive = Join-Path $ArtifactRoot "$($side.name)-source.zip"
        Invoke-Checked git @('-C', $repo, 'archive', '--format=zip', "--output=$archive", $side.revision) `
            (Join-Path $ArtifactRoot "$($side.name)-archive.log")
        Expand-Archive -LiteralPath $archive -DestinationPath $side.source
        foreach ($file in $harnessFiles) {
            $destination = Join-Path (Join-Path $side.source $benchmarkRelative) $file.FullName.Substring($harnessRoot.Length + 1)
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
        # Adapt only the two non-shipping harness projects in the exported copies.
        foreach ($relative in @("$benchmarkRelative\FujiyNotepad.Benchmarks.csproj", $nativeRelative)) {
            $project = Join-Path $side.source $relative
            $text = [IO.File]::ReadAllText($project).Replace('<TargetFramework>net11.0</TargetFramework>',
                "<TargetFramework>$($side.name).0</TargetFramework>")
            [IO.File]::WriteAllText($project, $text)
        }
        Invoke-DotNet $side @('--info') (Join-Path $ArtifactRoot "$($side.name)-sdk.log")
        Invoke-DotNet $side @('publish', $nativeRelative, '-c', 'Release', '-r', 'win-x64', '-o', $side.native,
            '-v:minimal', "-bl:$(Join-Path $ArtifactRoot "$($side.name)-native.binlog")") `
            (Join-Path $ArtifactRoot "$($side.name)-native.log")
        Invoke-DotNet $side @('run', '-c', 'Release', '--project', $benchmarkRelative, '--',
            '--filter', '*EngineBenchmarks*', '--runtimes', "nativeaot$($side.runtime.Substring(0, 4))",
            '--ilCompilerVersion', $side.runtime, '--cli', $side.dotnet,
            '--job', 'Dry', '--exporters', 'FullJSON', '--keepFiles', '--wakeLock', 'None', '--stopOnFirstError',
            '--buildTimeout', '600', '--artifacts', (Join-Path $ArtifactRoot "smoke\$($side.name)-micro")) `
            (Join-Path $ArtifactRoot "$($side.name)-micro-smoke.log")
        Assert-MicroResults (Join-Path $ArtifactRoot "smoke\$($side.name)-micro") $side 1
    }
    $generator = Join-Path $sides[1].native 'NativeWorkloads.exe'
    $smokeData = Join-Path $ArtifactRoot 'smoke-data'
    Invoke-Checked $generator @('generate', $smokeData, '4194304') (Join-Path $ArtifactRoot 'smoke-data.log')
    foreach ($side in $sides) {
        foreach ($id in @('ascii', 'utf8', 'utf16le', 'utf32le')) {
            Invoke-Checked (Join-Path $side.native 'NativeWorkloads.exe') @('run', $smokeData, $id,
                (Join-Path $ArtifactRoot "smoke-$($side.name)-$id.json"), '1') `
                (Join-Path $ArtifactRoot "smoke-$($side.name)-$id.log")
            $smoke = Get-Content -LiteralPath (Join-Path $ArtifactRoot "smoke-$($side.name)-$id.json") -Raw | ConvertFrom-Json
            if ($smoke.Runtime -ne $side.runtime -or $smoke.DynamicCodeSupported -or $smoke.Samples.Count -ne 11) {
                throw "Unexpected runtime, compilation mode or sample count for $($side.name)."
            }
        }
    }
    $data = Join-Path $ArtifactRoot 'data'
    Invoke-Checked $generator @('generate', $data, $WorkloadBytes.ToString([Globalization.CultureInfo]::InvariantCulture)) `
        (Join-Path $ArtifactRoot 'data-generation.log')
    Invoke-Checked $generator @('verify', $data) (Join-Path $ArtifactRoot 'data-verification.log')
    Save-Json ([ordered]@{
        formatVersion = 1
        preparedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        workloadBytes = $WorkloadBytes
        benchmarkDotNet = '0.16.0-preview.2'
        harnessFiles = $hashes
        sides = $sides
    }) $manifestPath
    Write-Host 'Preparation and compatibility/correctness smoke complete. No smoke timing is scored.'
    return
}

if (!$MeasurementApproved) { throw 'Obtain a quiet measurement slot, then pass -MeasurementApproved. Do not measure alongside builds/UI sweeps.' }
Assert-MeasurementSlot
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$resultsRoot = Join-Path $ArtifactRoot $Phase.ToLowerInvariant()
if (Test-Path -LiteralPath $resultsRoot) {
    if (!$Resume) { throw "Result directory already exists: $resultsRoot. Use -Resume to preserve completed batches." }
    $configuration = Get-Content -LiteralPath (Join-Path $resultsRoot 'configuration.json') -Raw | ConvertFrom-Json
    $savedExclusions = @()
    if ($configuration.PSObject.Properties['excludeCharacterCountFor']) {
        $savedExclusions = @($configuration.excludeCharacterCountFor)
    }
    elseif ($configuration.PSObject.Properties['excludeUtf8CharacterCount'] -and $configuration.excludeUtf8CharacterCount) {
        $savedExclusions = @('utf8')
    }
    if ($configuration.phase -ne $Phase -or $configuration.rounds -ne $Rounds -or $configuration.diskIterations -ne $DiskIterations) {
        throw 'Resume must use the original phase, round count and disk iteration count.'
    }
    if ((@($ExcludeCharacterCountFor | Sort-Object -Unique) -join ',') -ne (@($savedExclusions | Sort-Object -Unique) -join ',')) {
        throw 'Resume must use the original character-count exclusion.'
    }
}
else {
    New-Item -ItemType Directory -Path $resultsRoot | Out-Null
    Save-Json ([ordered]@{ phase = $Phase; rounds = $Rounds; diskIterations = $DiskIterations;
        measurementApproved = $true; excludeCharacterCountFor = @($ExcludeCharacterCountFor | Sort-Object -Unique) }) `
        (Join-Path $resultsRoot 'configuration.json')
}
$hostPrefix = if ($Resume) { 'host-resume-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') } else { 'host' }
Record-Host (Join-Path $resultsRoot "$hostPrefix-before.json")
for ($round = 0; $round -lt $Rounds; $round++) {
    # AB/BA ordering controls drift better than collecting all .NET 10 samples first.
    $orderedSides = if ($round % 2 -eq 0) { @($manifest.sides[0], $manifest.sides[1]) } else { @($manifest.sides[1], $manifest.sides[0]) }
    foreach ($side in $orderedSides) {
        Assert-MeasurementSlot
        if ($Phase -eq 'Micro') {
            $batchRoot = Join-Path $resultsRoot "$round-$($side.name)"
            if ($Resume -and (Test-Path -LiteralPath (Join-Path $batchRoot 'results\FujiyNotepad.Benchmarks.EngineBenchmarks-report-full.json'))) {
                Assert-MicroResults $batchRoot $side 10
                Write-Host "Keeping completed micro batch $round-$($side.name)."
                continue
            }
            if ($Resume) {
                Preserve-Interrupted $batchRoot
                Preserve-Interrupted (Join-Path $resultsRoot "$round-$($side.name).log")
            }
            Invoke-DotNet $side @('run', '-c', 'Release', '--no-build', '--project', $benchmarkRelative, '--',
                '--filter', '*EngineBenchmarks*', '--runtimes', "nativeaot$($side.runtime.Substring(0, 4))",
                '--ilCompilerVersion', $side.runtime, '--cli', $side.dotnet,
                '--launchCount', '1', '--warmupCount', '5', '--iterationCount', '10', '--iterationTime', '250',
                '--outliers', 'DontRemove', '--exporters', 'FullJSON', 'CSVMeasurements', '--keepFiles',
                '--generateBinLog', '--wakeLock', 'None', '--stopOnFirstError', '--buildTimeout', '600',
                '--artifacts', $batchRoot) `
                (Join-Path $resultsRoot "$round-$($side.name).log")
            Assert-MicroResults (Join-Path $resultsRoot "$round-$($side.name)") $side 10
        }
        else {
            foreach ($id in @('ascii', 'utf8', 'utf16le', 'utf32le')) {
                Assert-MeasurementSlot
                $resultPath = Join-Path $resultsRoot "$round-$($side.name)-$id.json"
                $excludeCount = $id -in $ExcludeCharacterCountFor
                $operationCount = if ($excludeCount) { 10 } else { 11 }
                if ($Resume -and (Test-Path -LiteralPath $resultPath)) {
                    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
                    if ($result.Runtime -ne $side.runtime -or $result.DynamicCodeSupported -or $result.Workload.Id -ne $id -or
                        $result.Samples.Count -ne $operationCount * $DiskIterations -or
                        @($result.Samples | Group-Object Operation | Where-Object Count -ne $DiskIterations).Count -ne 0) {
                        throw "Cannot resume from an invalid disk result: $resultPath"
                    }
                    if (($excludeCount -and ($result.ExcludedOperations.Count -ne 1 -or $result.ExcludedOperations[0] -ne 'CountCharacters')) -or
                        (!$excludeCount -and $result.ExcludedOperations.Count -ne 0)) {
                        throw "Unexpected excluded operations: $resultPath"
                    }
                    Write-Host "Keeping completed disk batch $round-$($side.name)-$id."
                    continue
                }
                if ($Resume) { Preserve-Interrupted (Join-Path $resultsRoot "$round-$($side.name)-$id.log") }
                $nativeArguments = @('run',
                    (Join-Path $ArtifactRoot 'data'), $id, $resultPath,
                    $DiskIterations.ToString([Globalization.CultureInfo]::InvariantCulture))
                if ($excludeCount) { $nativeArguments += '--exclude-character-count' }
                Invoke-Checked (Join-Path $side.native 'NativeWorkloads.exe') $nativeArguments `
                    (Join-Path $resultsRoot "$round-$($side.name)-$id.log")
            }
        }
    }
}
Record-Host (Join-Path $resultsRoot "$hostPrefix-after.json")
