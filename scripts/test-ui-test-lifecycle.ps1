#requires -Version 7.0
<#
.SYNOPSIS
    Dependency-free, headless regression tests for the UI harness lifecycle.
.DESCRIPTION
    Process operations and CLI settings-path resolution are fakes. Every filesystem operation is confined
    to an absolute, disposable test directory. LOCALAPPDATA deliberately differs from the mocked known
    folder; TEMP and TMP are isolated too. The default resolver test checks paths without opening settings.
    No viewer, winapp command, clipboard operation or real process termination is allowed.
#>
$ErrorActionPreference = 'Stop'
$lifecycle = Join-Path $PSScriptRoot 'ui-test-lifecycle.ps1'
. $lifecycle
$harness = Join-Path $PSScriptRoot 'ui-tests.ps1'
$results = [System.Collections.Generic.List[object]]::new()
$root = [System.IO.Directory]::CreateDirectory(
    (Join-Path ([System.IO.Path]::GetTempPath()) ("fujiy-lifecycle-tests-" + [guid]::NewGuid().ToString('N')))).FullName
$savedEnvironment = @{}
foreach ($name in 'LOCALAPPDATA', 'TEMP', 'TMP', 'WINAPP_CLI_TELEMETRY_OPTOUT') {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function New-FakeProcess {
    $process = [pscustomobject]@{
        Id = 123456789
        StartInfo = [System.Diagnostics.ProcessStartInfo]::new()
        Exited = $false
        ExitCode = 0
        Started = $false
        StartMode = 'success'
        StopMode = 'success'
        WaitMode = 'success'
        WaitConfirmed = $false
        Disposed = $false
        Events = [System.Collections.Generic.List[string]]::new()
    }
    $process | Add-Member ScriptProperty HasExited { return $this.Exited }
    $process | Add-Member ScriptMethod Start {
        $this.Events.Add('Start')
        if ($this.StartMode -eq 'throw') { throw 'mock launch failure' }
        if ($this.StartMode -eq 'false') { return $false }
        $this.Started = $true
        return $true
    }
    $process | Add-Member ScriptMethod WaitForExit {
        param([int]$Milliseconds)
        $this.Events.Add('Wait')
        if ($Milliseconds -ne 10000) { throw "Unexpected wait bound: $Milliseconds" }
        if ($this.WaitMode -eq 'throw') { throw 'mock wait failure' }
        if ($this.WaitMode -eq 'timeout') { return $false }
        $this.Exited = $true
        $this.WaitConfirmed = $true
        return $true
    }
    $process | Add-Member ScriptMethod Dispose {
        $this.Events.Add('Dispose')
        $this.Disposed = $true
    }
    return $process
}

# Command shadowing keeps the production code under test without a mocking dependency.
function New-Object([string]$TypeName) {
    Check ($TypeName -eq 'System.Diagnostics.Process') "Unexpected object creation: $TypeName"
    if ($case.CreationFails) { throw 'mock process allocation failure' }
    return $case.Process
}
function Get-Process { return $case.OtherProcesses }
function Stop-Process {
    [CmdletBinding()]
    param([int]$Id, [switch]$Force)
    $case.StopIds.Add($Id)
    Check ($Id -eq $case.Process.Id -and $Force) "Attempt to stop an unowned process: $Id"
    $case.Process.Events.Add('Stop')
    switch ($case.Process.StopMode) {
        'race' {
            $case.Process.Exited = $true
            $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
                [InvalidOperationException]::new('mock missing PID'), 'NoProcessFoundForGivenId',
                [System.Management.Automation.ErrorCategory]::ObjectNotFound, $Id))
        }
        'missing-live' {
            $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
                [InvalidOperationException]::new('mock missing PID'), 'NoProcessFoundForGivenId',
                [System.Management.Automation.ErrorCategory]::ObjectNotFound, $Id))
        }
        'fail-exited' {
            $case.Process.Exited = $true
            throw [System.ComponentModel.Win32Exception]::new(5, 'mock stop failure')
        }
        'fail-live' { throw [System.ComponentModel.Win32Exception]::new(5, 'mock stop failure') }
        'still-running' { return }
        default { $case.Process.Exited = $true }
    }
}
function Start-Process { throw 'Real process launch is forbidden in lifecycle tests.' }
function Start-Sleep { }
function winapp { throw 'mock UI failure; no UI or clipboard operations are allowed' }

function Check-TestPath([string]$Path) {
    Check ([System.IO.Path]::IsPathFullyQualified($Path)) "Path must be absolute: $Path"
    Check ($Path.StartsWith($case.Directory + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) "Path escaped the test directory: $Path"
}
function Copy-Item {
    [CmdletBinding()]
    param([string]$LiteralPath, [string]$Destination, [switch]$Force)
    Check-TestPath $LiteralPath
    Check-TestPath $Destination
    if ($Destination -eq $case.SettingsPath) {
        $case.RestoreAttempts++
        Check (-not $case.Process.Started -or $case.Process.WaitConfirmed) 'Restoration preceded confirmed exit.'
        if ($case.RestoreMode -eq 'throw') { throw 'mock restore failure' }
        $case.Process.Events.Add('Restore')
    } else {
        $case.Backups.Add($Destination)
        if ($case.BackupFails) {
            [System.IO.File]::WriteAllText($Destination, 'partial backup')
            throw 'mock backup failure'
        }
    }
    Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
    if ($Destination -eq $case.SettingsPath -and $case.RestoreMode -eq 'corrupt') {
        [System.IO.File]::AppendAllText($Destination, 'corrupted restoration')
    }
}
function Remove-Item {
    [CmdletBinding()]
    param([string]$LiteralPath, [switch]$Force)
    Check-TestPath $LiteralPath
    if ($LiteralPath -eq $case.SettingsPath) {
        $case.RestoreAttempts++
        Check (-not $case.Process.Started -or $case.Process.WaitConfirmed) 'Settings deletion preceded confirmed exit.'
    }
    if (($case.RemoveFailure -eq 'settings' -and $LiteralPath -eq $case.SettingsPath) -or
        ($case.RemoveFailure -eq 'sample' -and $LiteralPath -eq $case.Run.SamplePath) -or
        ($case.RemoveFailure -eq 'backup' -and $LiteralPath -in $case.Backups)) {
        throw "mock $($case.RemoveFailure) removal failure"
    }
    Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters
}

function Invoke-Case([string]$Name, [scriptblock]$Test) {
    $directory = [System.IO.Directory]::CreateDirectory((Join-Path $root ([guid]::NewGuid().ToString('N')))).FullName
    $env:LOCALAPPDATA = Join-Path $directory 'overridden-local'
    $env:TEMP = $directory
    $env:TMP = $directory
    $settings = Join-Path $directory 'known-folder\FujiyNotepad\settings.json'
    [System.IO.Directory]::CreateDirectory((Split-Path $settings -Parent)) | Out-Null
    $original = [byte[]](239, 187, 191) + [System.Text.Encoding]::UTF8.GetBytes("{`r`n  `"Theme`": `"Dark`"`r`n}`r`n")
    [System.IO.File]::WriteAllBytes($settings, $original)
    $exe = Join-Path $directory 'viewer [test].exe'
    [System.IO.File]::WriteAllText($exe, 'Not an executable; even a broken mock cannot launch the viewer.')
    $case = @{
        Directory = $directory
        SettingsPath = $settings
        Original = [Convert]::ToBase64String($original)
        Exe = $exe
        Process = New-FakeProcess
        OtherProcesses = @()
        StopIds = [System.Collections.Generic.List[int]]::new()
        Backups = [System.Collections.Generic.List[string]]::new()
        RestoreAttempts = 0
        RestoreMode = 'success'
        BackupFails = $false
        CreationFails = $false
        RemoveFailure = ''
        Run = New-UiTestRun -SettingsPath $settings
        Runs = [System.Collections.Generic.List[object]]::new()
    }
    $case.Runs.Add($case.Run)
    try {
        & $Test
        $results.Add([pscustomobject]@{ Name = $Name; Passed = $true; Detail = '' })
    } catch {
        $results.Add([pscustomobject]@{ Name = $Name; Passed = $false; Detail = $_.ToString() })
    } finally {
        foreach ($run in $case.Runs) {
            if ($run.Guard) { $run.Guard.Dispose() }
        }
    }
}

function Invoke-TestRun {
    $setupError = $null
    $cleanupError = $null
    try {
        $null = Start-UiTestRun -Run $case.Run -Exe $case.Exe -SampleLines @('alpha', 'beta') 6>$null
        [System.IO.File]::WriteAllText($case.SettingsPath, 'test-modified settings')
    } catch {
        $setupError = $_
    } finally {
        try { Complete-UiTestRun -Run $case.Run 6>$null } catch { $cleanupError = $_ }
    }
    return @{ SetupError = $setupError; CleanupError = $cleanupError }
}

function Invoke-TestHarness {
    $scripts = [System.IO.Directory]::CreateDirectory((Join-Path $case.Directory 'cli-harness')).FullName
    $entryPoint = Join-Path $scripts 'ui-tests.ps1'
    [System.IO.File]::Copy($harness, $entryPoint, $true)
    # Keep the entry point unchanged, but replace its default known-folder lookup with the fixture.
    # Reject an explicit unexpected path before any guard/directory acquisition, even on regression.
    $mock = ". '$($lifecycle.Replace("'", "''"))'`n" + @'
$realNewUiTestRun = ${function:New-UiTestRun}
function New-UiTestRun([string]$SettingsPath = $case.SettingsPath) {
    Check ($SettingsPath -ceq $case.SettingsPath) 'CLI settings path bypassed the mocked Windows known folder.'
    & $realNewUiTestRun -SettingsPath $SettingsPath
}
'@
    [System.IO.File]::WriteAllText((Join-Path $scripts 'ui-test-lifecycle.ps1'), $mock)
    $output = (& $entryPoint -Exe $case.Exe -ArtifactDir $case.Directory *>&1 | Out-String)
    return @{ Output = $output; ExitCode = $LASTEXITCODE }
}

function Check-Original {
    Check (([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($case.SettingsPath))) -ceq $case.Original) 'Original settings bytes were not preserved.'
}
function Check-Cleaned {
    Check (-not (Test-Path -LiteralPath $case.Run.SamplePath)) 'Sample leaked.'
    if ($case.Process.StartInfo.ArgumentList.Count -gt 0) {
        Check (-not (Test-Path -LiteralPath $case.Process.StartInfo.ArgumentList[0])) 'CLI launch sample leaked.'
    }
    foreach ($backup in $case.Backups) { Check (-not (Test-Path -LiteralPath $backup)) "Backup leaked: $backup" }
    Check (-not (Test-Path -LiteralPath $case.Run.GuardPath)) 'Guard was not released.'
    if ($case.Run.Process -or $case.Process.Events.Contains('Start')) {
        Check $case.Process.Disposed 'Owned process handle was not disposed.'
    }
}
function Check-Recovery([string]$ErrorText) {
    Check (Test-Path -LiteralPath $case.Run.GuardPath) 'Recovery guard was lost.'
    Check ($ErrorText.Contains($case.Run.GuardPath)) 'Recovery diagnostics omit the guard path.'
    Check ($ErrorText.Contains($case.SettingsPath)) 'Recovery diagnostics omit the settings path.'
    if ($case.Run.SettingsExisted) {
        Check (([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($case.Run.BackupPath))) -ceq $case.Original) 'Recovery backup was lost or corrupted.'
        Check ($ErrorText.Contains($case.Run.BackupPath)) 'Recovery diagnostics omit the backup path.'
    } else {
        Check ($ErrorText.Contains('there was no original')) 'Recovery omits the originally absent settings state.'
    }
    Check (-not (Test-Path -LiteralPath $case.Run.SamplePath)) 'Unsafe restoration skipped sample cleanup.'
    # The stream is released, but CreateNew must still reject this retained recovery marker.
    $stream = [System.IO.File]::Open($case.Run.GuardPath, 'Open', 'Read', 'None')
    $stream.Dispose()
    $recovery = [System.IO.File]::ReadAllText($case.Run.GuardPath)
    Check ($recovery.Contains($case.SettingsPath) -and
        $recovery.Contains("Originally existed: $($case.Run.SettingsExisted)") -and
        $recovery.Contains("Owned PID: $($case.Process.Id)")) 'Guard lost persistent recovery details.'
    if ($case.Run.SettingsExisted) {
        Check ($recovery.Contains($case.Run.BackupPath)) 'Guard lost the recovery backup path.'
    }
}

try {
    Invoke-Case 'Default settings and guard paths ignore LOCALAPPDATA overrides' {
        $expected = [System.IO.Path]::Combine(
            [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData),
            'FujiyNotepad', 'settings.json')
        foreach ($override in 'override-one', 'override-two') {
            $env:LOCALAPPDATA = Join-Path $case.Directory $override
            $resolved = New-UiTestRun
            Check ($resolved.SettingsPath -ceq $expected) 'Settings path differs from the viewer Windows known folder.'
            Check ($resolved.GuardPath -ceq "$expected.ui-tests.lock") 'Settings guard changed with an environment override.'
        }
    }
    Invoke-Case 'Unresolved or relative settings paths fail before resource acquisition' {
        foreach ($invalidPath in '', 'settings.json') {
            $errorText = ''
            try { $null = New-UiTestRun -SettingsPath $invalidPath } catch { $errorText = "$_" }
            Check ($errorText -match 'settings path must be absolute') 'An unresolved settings path was accepted.'
        }
    }
    Invoke-Case 'Existing settings restored byte-for-byte, only owned PID stopped' {
        $case.OtherProcesses = @([pscustomobject]@{ Id = 987654321; ProcessName = 'FujiyNotepad.Other'; Exited = $false })
        $result = Invoke-TestRun
        Check (-not $result.SetupError -and -not $result.CleanupError) "$($result.SetupError) $($result.CleanupError)"
        Check ($case.StopIds.Count -eq 1 -and $case.StopIds[0] -eq $case.Process.Id) 'Cleanup did not target exactly the owned PID.'
        Check (-not $case.OtherProcesses[0].Exited) 'Unrelated process was touched.'
        Check (($case.Process.Events -join ',') -eq 'Start,Stop,Wait,Restore,Dispose') 'Lifecycle order changed.'
        Check-Original
        Check-Cleaned
    }
    Invoke-Case 'Originally absent settings removed only after confirmed exit' {
        [System.IO.File]::Delete($case.SettingsPath)
        $result = Invoke-TestRun
        Check (-not $result.SetupError -and -not $result.CleanupError) "$($result.SetupError) $($result.CleanupError)"
        Check (-not (Test-Path -LiteralPath $case.SettingsPath)) 'Test-created settings remain.'
        Check ($case.Backups.Count -eq 0) 'Absent settings created a spurious backup.'
        Check-Cleaned
    }
    Invoke-Case 'Already-exited process needs no stop but still confirms exit' {
        $case.Process.Exited = $true
        $result = Invoke-TestRun
        Check (-not $result.CleanupError) "$($result.CleanupError)"
        Check ($case.StopIds.Count -eq 0 -and $case.Process.WaitConfirmed) 'Already-exited process handling failed.'
        Check-Original
        Check-Cleaned
    }
    Invoke-Case 'Exit between HasExited and Stop-Process is benign' {
        $case.Process.StopMode = 'race'
        $result = Invoke-TestRun
        Check (-not $result.CleanupError) "$($result.CleanupError)"
        Check-Original
        Check-Cleaned
    }
    foreach ($mode in 'fail-exited', 'missing-live') {
        Invoke-Case "Genuine stop error remains a failure after eventual exit: $mode" {
            $case.Process.StopMode = $mode
            $result = Invoke-TestRun
            Check ($result.CleanupError -match 'Could not stop owned PID') 'Stop failure was swallowed.'
            Check-Original
            Check-Cleaned
        }
    }
    foreach ($mode in 'throw', 'timeout') {
        Invoke-Case "Unconfirmed exit preserves backup and guard: $mode" {
            $case.Process.WaitMode = $mode
            $result = Invoke-TestRun
            Check ($result.CleanupError -match 'Could not confirm exit') 'Wait failure was swallowed.'
            Check ($case.RestoreAttempts -eq 0) 'Unconfirmed exit overwrote settings.'
            Check-Recovery "$($result.CleanupError)"
        }
    }
    Invoke-Case 'Stop and wait failures are both reported without touching live settings' {
        $case.Process.StopMode = 'fail-live'
        $case.Process.WaitMode = 'timeout'
        $result = Invoke-TestRun
        Check ($result.CleanupError -match 'mock stop failure' -and $result.CleanupError -match 'Timed out') 'A teardown failure hid another.'
        Check ($case.RestoreAttempts -eq 0) 'Live process settings were overwritten.'
        Check ([System.IO.File]::ReadAllText($case.SettingsPath) -eq 'test-modified settings') 'Live settings changed during cleanup.'
        Check-Recovery "$($result.CleanupError)"
    }
    Invoke-Case 'Unconfirmed exit never deletes originally absent settings while the app is alive' {
        [System.IO.File]::Delete($case.SettingsPath)
        $case.Process.StopMode = 'still-running'
        $case.Process.WaitMode = 'timeout'
        $result = Invoke-TestRun
        Check ($result.CleanupError -match 'Could not confirm exit') 'Wait failure was swallowed.'
        Check ($case.RestoreAttempts -eq 0 -and (Test-Path -LiteralPath $case.SettingsPath)) 'Live process settings were deleted.'
        Check-Recovery "$($result.CleanupError)"
    }
    foreach ($mode in 'throw', 'corrupt') {
        Invoke-Case "Failed restoration retains usable recovery files: $mode" {
            $case.RestoreMode = $mode
            $result = Invoke-TestRun
            Check ($result.CleanupError -match 'Could not restore settings') 'Restore failure was swallowed.'
            Check-Recovery "$($result.CleanupError)"
            $retry = New-UiTestRun -SettingsPath $case.SettingsPath
            $case.Runs.Add($retry)
            $errorText = ''
            try { $null = Start-UiTestRun $retry $case.Exe @('sample') 6>$null } catch { $errorText = "$_" }
            finally { Complete-UiTestRun $retry 6>$null }
            Check ($errorText -match 'Cannot acquire UI-test settings guard') 'Pending recovery did not block a new run.'
            Check-Recovery "$($result.CleanupError)"
        }
    }
    Invoke-Case 'Failed removal of originally absent settings retains recovery marker' {
        [System.IO.File]::Delete($case.SettingsPath)
        $case.RemoveFailure = 'settings'
        $result = Invoke-TestRun
        Check ($result.CleanupError -match 'mock settings removal failure') 'Settings deletion failure was swallowed.'
        Check (Test-Path -LiteralPath $case.SettingsPath) 'Fixture did not exercise a failed deletion.'
        Check-Recovery "$($result.CleanupError)"
    }
    Invoke-Case 'Sample deletion failure does not prevent settings restoration' {
        $case.RemoveFailure = 'sample'
        $result = Invoke-TestRun
        Check ($result.CleanupError -match 'mock sample removal failure') 'Sample deletion failure was swallowed.'
        Check-Original
        Check (Test-Path -LiteralPath $case.Run.SamplePath) 'Fixture did not exercise a failed deletion.'
        Check (-not (Test-Path -LiteralPath $case.Run.GuardPath)) 'Safe settings restoration did not release guard.'
    }
    Invoke-Case 'Backup deletion failure is explicit after successful restoration' {
        $case.RemoveFailure = 'backup'
        $result = Invoke-TestRun
        Check ($result.CleanupError -match 'mock backup removal failure') 'Backup deletion failure was swallowed.'
        Check-Original
        Check (Test-Path -LiteralPath $case.Run.BackupPath) 'Fixture did not retain the backup.'
        Check (-not (Test-Path -LiteralPath $case.Run.SamplePath)) 'Sample leaked.'
    }
    Invoke-Case 'Existing viewer blocks setup without becoming a termination target' {
        $case.OtherProcesses = @([pscustomobject]@{ Id = 987654321; ProcessName = 'FujiyNotepad.WinUI'; Exited = $false })
        $result = Invoke-TestRun
        Check ($result.SetupError -match 'Close existing FujiyNotepad instances') 'Existing viewer was not detected.'
        Check (-not $result.CleanupError -and $case.StopIds.Count -eq 0) 'Refused setup attempted process termination.'
        Check ($case.Backups.Count -eq 0 -and -not $case.Process.Started) 'Refused setup acquired app resources.'
        Check-Original
        Check-Cleaned
    }
    Invoke-Case 'Partial backup failure cleans up without changing original settings' {
        $case.BackupFails = $true
        $result = Invoke-TestRun
        Check ($result.SetupError -match 'mock backup failure') 'Backup failure was not exercised.'
        Check (-not $result.CleanupError -and -not $case.Process.Started) 'Backup failure launched a process or broke cleanup.'
        Check-Original
        Check-Cleaned
    }
    Invoke-Case 'Sample creation failure is inside the cleanup boundary' {
        $case.Run.SamplePath = Join-Path $case.Directory 'missing\sample.txt'
        $result = Invoke-TestRun
        Check ($null -ne $result.SetupError) 'Sample creation unexpectedly succeeded.'
        Check (-not $result.CleanupError -and -not $case.Process.Started) 'Sample failure broke cleanup.'
        Check-Original
        Check-Cleaned
    }
    Invoke-Case 'Process allocation failure cleans up acquired files' {
        $case.CreationFails = $true
        $result = Invoke-TestRun
        Check ($result.SetupError -match 'mock process allocation failure') 'Allocation failure was not exercised.'
        Check (-not $result.CleanupError) "$($result.CleanupError)"
        Check-Original
        Check-Cleaned
    }
    foreach ($mode in 'throw', 'false') {
        Invoke-Case "Launch failure restores settings and releases resources: $mode" {
            $case.Process.StartMode = $mode
            $result = Invoke-TestRun
            Check ($null -ne $result.SetupError) 'Launch failure was not exercised.'
            Check (-not $result.CleanupError -and $case.StopIds.Count -eq 0) 'Failed launch tried to stop a process.'
            Check-Original
            Check-Cleaned
        }
    }
    foreach ($existing in $true, $false) {
        Invoke-Case "Overlapping acquisition fails before mutation; release permits retry (existing=$existing)" {
            if (-not $existing) { [System.IO.File]::Delete($case.SettingsPath) }
            $first = $case.Run
            try {
                $null = Start-UiTestRun $first $case.Exe @('sample') 6>$null
                [System.IO.File]::WriteAllText($case.SettingsPath, 'first run settings')
                $second = New-UiTestRun -SettingsPath $case.SettingsPath
                $case.Runs.Add($second)
                $errorText = ''
                try { $null = Start-UiTestRun $second $case.Exe @('sample') 6>$null } catch { $errorText = "$_" }
                finally { Complete-UiTestRun $second 6>$null }
                Check ($errorText -match 'Cannot acquire UI-test settings guard') 'Overlapping run acquired the guard.'
                Check (-not $second.SettingsCaptured -and -not $second.ProcessStarted) 'Overlapping run captured settings or started a process.'
                Check ($case.StopIds.Count -eq 0) 'Second run stopped the first process.'
                Check ([System.IO.File]::ReadAllText($case.SettingsPath) -eq 'first run settings') 'Second run overwrote first-run settings.'
                Check (Test-Path -LiteralPath $first.GuardPath) 'Second run deleted the first guard.'
            } finally { Complete-UiTestRun $first 6>$null }
            if ($existing) { Check-Original } else { Check (-not (Test-Path -LiteralPath $case.SettingsPath)) 'Absent settings were not restored.' }
            Check-Cleaned
            $case.Run = New-UiTestRun -SettingsPath $case.SettingsPath
            $case.Runs.Add($case.Run)
            $case.Process = New-FakeProcess
            $result = Invoke-TestRun
            Check (-not $result.SetupError -and -not $result.CleanupError) 'Released guard prevented the next run.'
            Check-Cleaned
        }
    }
    Invoke-Case 'CLI runs with different LOCALAPPDATA overrides reject the same occupied guard' {
        [System.IO.File]::WriteAllText($case.Run.GuardPath, 'another run or pending recovery')
        foreach ($override in 'override-one', 'override-two') {
            $env:LOCALAPPDATA = Join-Path $case.Directory $override
            $result = Invoke-TestHarness
            $output = $result.Output
            Check ($result.ExitCode -eq 1 -and $output -match 'Cannot acquire UI-test settings guard') "Unexpected CLI result: $output"
            Check ($case.Backups.Count -eq 0 -and $case.StopIds.Count -eq 0 -and -not $case.Process.Started) 'Refused CLI run acquired app resources.'
            Check ([System.IO.File]::ReadAllText($case.Run.GuardPath) -eq 'another run or pending recovery') 'Refused CLI run changed the existing guard.'
            Check (-not (Test-Path -LiteralPath $env:LOCALAPPDATA)) 'CLI created state in the environment override.'
            Check-Original
        }
    }
    Invoke-Case 'CLI launch failure is nonzero and runs cleanup' {
        $case.Process.StartMode = 'throw'
        $result = Invoke-TestHarness
        $output = $result.Output
        Check ($result.ExitCode -eq 1 -and $output -match 'mock launch failure' -and $output -match 'UI tests FAILED') "Unexpected CLI result: $output"
        Check ($case.Backups.Count -eq 1) 'CLI did not back up the mocked known-folder settings.'
        Check-Original
        Check-Cleaned
    }
    Invoke-Case 'CLI preserves both UI failure and cleanup failure, with recovery details' {
        $case.Process.WaitMode = 'throw'
        $result = Invoke-TestHarness
        $output = $result.Output
        Check ($result.ExitCode -eq 1 -and $output -match 'mock UI failure' -and $output -match 'mock wait failure') "CLI lost a failure: $output"
        Check ($output -match 'CLEANUP ERROR' -and $output -notmatch 'UI tests PASSED') 'CLI reported false success.'
        Check ($case.RestoreAttempts -eq 0 -and $case.Backups.Count -eq 1) 'CLI did not preserve the snapshot.'
        Check (Test-Path -LiteralPath $case.Backups[0]) 'CLI destroyed the recovery backup.'
        Check ($output.Contains($case.Backups[0]) -and $output.Contains($case.Run.GuardPath)) 'CLI omitted recovery paths.'
        Check (-not (Test-Path -LiteralPath $case.Process.StartInfo.ArgumentList[0])) 'CLI cleanup failure skipped sample removal.'
        Check $case.Process.Disposed 'CLI cleanup failure leaked its process handle.'
    }
} finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $root -Recurse -Force
}

foreach ($result in $results) {
    "{0}  {1}{2}" -f $(if ($result.Passed) { 'PASS' } else { 'FAIL' }), $result.Name,
        $(if ($result.Detail) { ": $($result.Detail)" } else { '' })
}
$failed = @($results | Where-Object { -not $_.Passed }).Count
"`n$($results.Count - $failed)/$($results.Count) passed"
if ($failed -gt 0) { exit 1 }
exit 0
