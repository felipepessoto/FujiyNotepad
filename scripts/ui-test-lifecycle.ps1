#requires -Version 7.0

# Match SettingsStore.Default: LOCALAPPDATA overrides do not redirect the Windows known folder.
function New-UiTestRun {
    param(
        [string]$SettingsPath = [System.IO.Path]::Combine(
            [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData),
            'FujiyNotepad', 'settings.json')
    )
    if (-not [System.IO.Path]::IsPathFullyQualified($SettingsPath)) {
        throw "UI-test settings path must be absolute. Got: '$SettingsPath'."
    }
    return @{
        SettingsPath = $SettingsPath
        SettingsCaptured = $false
        SettingsExisted = $false
        BackupPath = $null
        GuardPath = "$SettingsPath.ui-tests.lock"
        Guard = $null
        SamplePath = Join-Path ([System.IO.Path]::GetTempPath()) ("fujiy-uitests-{0}.txt" -f [guid]::NewGuid().ToString('N'))
        Process = $null
        ProcessStarted = $false
    }
}

function Get-UiTestProcessOwnerSid([int]$ProcessId) {
    try {
        $process = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
        # A viewer can exit between enumeration and this read-only lookup.
        if ($null -eq $process) { return $null }
        $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid -ErrorAction Stop
        if ($owner.ReturnValue -ne 0 -or [string]::IsNullOrWhiteSpace($owner.Sid)) {
            throw "GetOwnerSid returned code '$($owner.ReturnValue)' or no owner SID."
        }
        return $owner.Sid
    } catch {
        throw "Cannot determine owner of PID $ProcessId; refusing to assume it belongs to another Windows user. $($_.Exception.Message)"
    }
}

# The caller must put this entire acquisition inside try/finally with Complete-UiTestRun.
function Start-UiTestRun($Run, [string]$Exe, [string[]]$SampleLines) {
    [System.IO.Directory]::CreateDirectory((Split-Path $Run.SettingsPath -Parent)) | Out-Null
    try {
        $Run.Guard = [System.IO.File]::Open(
            $Run.GuardPath, [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    } catch {
        throw "Cannot acquire UI-test settings guard '$($Run.GuardPath)'. Another run or unfinished recovery may own it; do not remove it while a run is active. $($_.Exception.Message)"
    }

    # Read-only preflight, not a source of termination targets. Other apps do not honor our guard.
    $appNames = @('FujiyNotepad.WinUI', [System.IO.Path]::GetFileNameWithoutExtension($Exe))
    $otherApps = @(Get-Process | Where-Object { $_.ProcessName -in $appNames })
    if ($otherApps.Count -gt 0) {
        $userSid = Get-UiTestProcessOwnerSid -ProcessId $PID
        if (-not $userSid) { throw "Cannot determine owner of harness PID $PID." }
        # Account SID, not session ID: the same user's other logon sessions can share settings.
        $otherApps = @($otherApps | Where-Object { (Get-UiTestProcessOwnerSid -ProcessId $_.Id) -eq $userSid })
    }
    if ($otherApps.Count -gt 0) {
        throw "Close existing FujiyNotepad instances for this Windows user, including other sessions, before running UI tests (detected PID(s): $($otherApps.Id -join ', ')). They share settings and will not be stopped by this harness."
    }

    $Run.SettingsExisted = Test-Path -LiteralPath $Run.SettingsPath
    if ($Run.SettingsExisted) {
        $Run.BackupPath = [System.IO.Path]::GetTempFileName()
        Copy-Item -LiteralPath $Run.SettingsPath -Destination $Run.BackupPath -Force -ErrorAction Stop
        Write-Host "Backed up settings.json to '$($Run.BackupPath)'"
    }
    $Run.SettingsCaptured = $true
    $recovery = "Settings: $($Run.SettingsPath)`nOriginally existed: $($Run.SettingsExisted)`nBackup: $($Run.BackupPath)`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($recovery)
    $Run.Guard.Write($bytes, 0, $bytes.Length)
    $Run.Guard.Flush($true)

    [System.IO.File]::WriteAllText($Run.SamplePath, ($SampleLines -join "`r`n"))
    Write-Host "Launching $Exe on $(Split-Path $Run.SamplePath -Leaf)"

    # Starting the retained Process directly keeps its native launch handle open, so the PID cannot
    # be reused before cleanup. Do not replace it with a later Get-Process lookup.
    $Run.Process = New-Object System.Diagnostics.Process
    $Run.Process.StartInfo.FileName = (Resolve-Path -LiteralPath $Exe).ProviderPath
    $Run.Process.StartInfo.UseShellExecute = $false
    $Run.Process.StartInfo.WorkingDirectory = (Get-Location).ProviderPath
    $Run.Process.StartInfo.ArgumentList.Add($Run.SamplePath)
    if (-not $Run.Process.Start()) { throw "Could not start '$Exe'." }
    $Run.ProcessStarted = $true
    $bytes = [System.Text.Encoding]::UTF8.GetBytes("Owned PID: $($Run.Process.Id)`n")
    $Run.Guard.Write($bytes, 0, $bytes.Length)
    $Run.Guard.Flush($true)
    return $Run.Process
}

function Complete-UiTestRun($Run) {
    $errors = [System.Collections.Generic.List[string]]::new()
    $exited = -not $Run.ProcessStarted
    $restored = -not $Run.SettingsCaptured

    try {
        if ($Run.ProcessStarted) {
            try {
                if (-not $Run.Process.HasExited) {
                    try {
                        Stop-Process -Id $Run.Process.Id -Force -ErrorAction Stop
                    } catch {
                        # Only a missing PID plus confirmation from the retained process is a benign race.
                        if ($_.FullyQualifiedErrorId -notlike 'NoProcessFoundForGivenId,*' -or
                            -not $Run.Process.HasExited) {
                            throw
                        }
                    }
                }
            } catch {
                $errors.Add("Could not stop owned PID $($Run.Process.Id): $($_.Exception.Message)")
            }

            # Still wait after a stop error: it may have exited independently, allowing safe restoration
            # without hiding the original teardown error.
            try {
                $exited = $Run.Process.WaitForExit(10000)
                if (-not $exited) { throw 'Timed out after 10 seconds.' }
            } catch {
                $exited = $false
                $errors.Add("Could not confirm exit of owned PID $($Run.Process.Id): $($_.Exception.Message)")
            }
        }

        if ($Run.SettingsCaptured -and $exited) {
            try {
                if ($Run.SettingsExisted) {
                    Copy-Item -LiteralPath $Run.BackupPath -Destination $Run.SettingsPath -Force -ErrorAction Stop
                    if ((Get-FileHash -LiteralPath $Run.SettingsPath).Hash -ne
                        (Get-FileHash -LiteralPath $Run.BackupPath).Hash) {
                        throw 'Restored settings do not match the backup.'
                    }
                    Write-Host 'Restored settings.json'
                } elseif (Test-Path -LiteralPath $Run.SettingsPath) {
                    Remove-Item -LiteralPath $Run.SettingsPath -Force -ErrorAction Stop
                }
                $restored = $true
            } catch {
                $errors.Add("Could not restore settings '$($Run.SettingsPath)': $($_.Exception.Message)")
            }
        }

        # Independent attempts ensure a stop/restore failure does not skip temporary-file cleanup.
        try {
            if (Test-Path -LiteralPath $Run.SamplePath) {
                Remove-Item -LiteralPath $Run.SamplePath -Force -ErrorAction Stop
            }
        } catch {
            $errors.Add("Could not remove sample '$($Run.SamplePath)': $($_.Exception.Message)")
        }
        if ($restored -and $Run.BackupPath) {
            try {
                Remove-Item -LiteralPath $Run.BackupPath -Force -ErrorAction Stop
            } catch {
                $errors.Add("Could not remove backup '$($Run.BackupPath)': $($_.Exception.Message)")
            }
        }
    } finally {
        if ($Run.Process) {
            try { $Run.Process.Dispose() } catch {
                $errors.Add("Could not dispose owned process: $($_.Exception.Message)")
            }
        }
        if ($Run.Guard) {
            try {
                $Run.Guard.Dispose()
                # CreateNew makes the closed marker fail-closed too, until recovery is complete.
                if ($exited -and $restored) {
                    Remove-Item -LiteralPath $Run.GuardPath -Force -ErrorAction Stop
                }
            } catch {
                $errors.Add("Could not release settings guard '$($Run.GuardPath)': $($_.Exception.Message)")
            }
        }
    }

    if (-not $restored) {
        $action = if ($Run.SettingsExisted) {
            "copy backup '$($Run.BackupPath)' to '$($Run.SettingsPath)', then remove the backup"
        } else {
            "remove the test-created settings file '$($Run.SettingsPath)' (there was no original)"
        }
        $errors.Add("Settings recovery required. After confirming the owned process and all other viewer instances for this Windows user are closed, $action, then remove guard '$($Run.GuardPath)'. Do not rerun UI tests before recovery.")
    }
    if ($errors.Count -gt 0) { throw ("UI-test cleanup failed:`n" + ($errors -join "`n")) }
}
