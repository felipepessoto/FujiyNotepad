#requires -Version 7.0
<#
.SYNOPSIS
    App-layer UI smoke test for FujiyNotepad, driven by the Windows App Development CLI (winapp ui).

.DESCRIPTION
    Launches the *published* WinUI app on a sample file and drives it through Windows UI Automation to
    assert the app-layer glue works end to end: the XAML loads, the window and menus render, the file opens
    and the status bar populates, the Find bar wires up, and the process does not crash. This is the one
    layer the FujiyNotepad.Core / FujiyNotepad.WinUI.Logic unit tests cannot cover (it needs a real desktop
    session and a built app), so it runs as a dedicated CI job rather than via `dotnet test`.

    It targets the published Native-AOT executable on purpose: this app's nastiest regressions (e.g. the
    CsWinRT file-picker CCW failure, missing resources.pri) only reproduce in the AOT build, so a smoke test
    against the managed build would miss them.

.PARAMETER Exe
    Full path to the published FujiyNotepad.WinUI.exe. Defaults to the FUJIY_APP_EXE environment variable.

.PARAMETER ArtifactDir
    Directory to write a failure screenshot into. Defaults to the system temp folder.

.NOTES
    Requires the winapp CLI on PATH (locally: `winget install Microsoft.winappcli`; in CI:
    the microsoft/setup-WinAppCli action).
#>
[CmdletBinding()]
param(
    [string]$Exe = $env:FUJIY_APP_EXE,
    [string]$ArtifactDir = $env:TEMP
)

$ErrorActionPreference = 'Stop'
$env:WINAPP_CLI_TELEMETRY_OPTOUT = '1'

if ([string]::IsNullOrWhiteSpace($Exe) -or -not (Test-Path $Exe)) {
    Write-Error "App executable not found. Pass -Exe <path> or set FUJIY_APP_EXE. Got: '$Exe'"
    exit 2
}

$script:failures = 0
function Assert([bool]$Condition, [string]$Message, [string]$Detail = '') {
    if ($Condition) {
        Write-Host "  [PASS] $Message"
    } else {
        Write-Host "  [FAIL] $Message" -ForegroundColor Red
        if ($Detail) { Write-Host "         $Detail" -ForegroundColor Red }
        $script:failures++
    }
}

# A small, known sample so the status-bar counts are deterministic. CRLF on purpose.
$sampleLines = @(
    'alpha first line',
    'beta second line with ERROR',
    'gamma third line',
    'delta fourth line',
    'epsilon fifth line'
)
$sample = Join-Path ([System.IO.Path]::GetTempPath()) ("fujiy-uismoke-{0}.txt" -f ([guid]::NewGuid().ToString('N')))
[System.IO.File]::WriteAllText($sample, ($sampleLines -join "`r`n"))
$sampleName = Split-Path $sample -Leaf

Write-Host "Launching $Exe on $sampleName"
$proc = Start-Process -FilePath $Exe -ArgumentList "`"$sample`"" -PassThru

function Cleanup {
    Get-Process FujiyNotepad* -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Remove-Item $sample -Force -ErrorAction SilentlyContinue
}

try {
    $id = $proc.Id

    # 1) The process stays up (an AOT activation crash would exit within a couple of seconds).
    Start-Sleep -Seconds 6
    Assert (-not $proc.HasExited) "Process is still running after launch" `
        "Process exited with code $($proc.ExitCode) — likely an activation/AOT crash."
    if ($proc.HasExited) { throw "App exited on launch; aborting smoke test." }

    # 2) The main window appears and carries the expected title (proves XAML loaded + file opened).
    $hwnd = $null
    $title = $null
    for ($i = 0; $i -lt 20 -and -not $hwnd; $i++) {
        $wins = (winapp ui list-windows -a $id --json 2>$null | ConvertFrom-Json)
        $main = $wins | Where-Object { $_.title -like '*Fujiy Notepad*' } | Select-Object -First 1
        if ($main) { $hwnd = $main.hwnd; $title = $main.title }
        else { Start-Sleep -Milliseconds 500 }
    }
    Assert ($null -ne $hwnd) "Main window is present" "No window titled '* Fujiy Notepad' appeared within 10s."
    if (-not $hwnd) { throw "No main window; aborting." }
    Assert ($title -like "*$sampleName*") "Title shows the opened file name" "Title was: '$title'"

    # 3) The menu bar rendered with its top-level menus.
    $menuText = (winapp ui inspect window -w $hwnd 2>$null | Out-String)
    foreach ($m in 'File', 'Edit', 'View', 'Encoding', 'Help') {
        Assert ($menuText -match "\b$m\b") "Menu '$m' is present"
    }

    # 4) The status bar populated from the opened file (line + character counts).
    $status = (winapp ui get-value 'LblStatus' -w $hwnd 2>$null | Out-String)
    Assert ($status -match '5\s+lines') "Status bar shows the line count (5 lines)" "Status was: '$($status.Trim())'"

    # 5) Menu wiring + accessibility: open Edit > Find and confirm the Find bar's option toggle exposes the
    #    AutomationProperties.Name added in the best-practices pass (proves the menu handler ran).
    winapp ui invoke 'Edit' -a $id 2>$null | Out-Null
    Start-Sleep -Milliseconds 700
    winapp ui invoke 'Find...' -a $id 2>$null | Out-Null
    Start-Sleep -Milliseconds 900
    $nameJson = (winapp ui get-property 'MatchCaseToggle' -w $hwnd --property Name --json 2>$null | Out-String)
    $matchName = $null
    try { $matchName = ($nameJson | ConvertFrom-Json).properties.Name } catch { }
    Assert ($matchName -eq 'Match case') "Find bar opened and the option toggle exposes its accessibility name" `
        "MatchCaseToggle Name was: '$matchName'"

    # 6) Typing into the find box works (proves the bar is live and focusable).
    winapp ui set-value 'FindBox' 'ERROR' -w $hwnd 2>$null | Out-Null
    Start-Sleep -Milliseconds 400
    $findVal = (winapp ui get-value 'FindBox' -w $hwnd 2>$null | Out-String)
    Assert ($findVal -match 'ERROR') "Find box accepts typed text" "FindBox value was: '$($findVal.Trim())'"

    # 7) Still alive after interaction.
    Assert (-not $proc.HasExited) "Process is still running after interaction"
}
catch {
    Write-Host "EXCEPTION: $_" -ForegroundColor Red
    $script:failures++
}
finally {
    # On failure, capture a screenshot for the CI artifact before tearing down.
    if ($script:failures -gt 0 -and $hwnd) {
        try {
            $shot = Join-Path $ArtifactDir 'ui-smoke-failure.png'
            winapp ui screenshot window -w $hwnd --output $shot 2>$null | Out-Null
            Write-Host "Saved failure screenshot to $shot"
        } catch { }
    }
    Cleanup
}

Write-Host ''
if ($script:failures -eq 0) {
    Write-Host "UI smoke test PASSED" -ForegroundColor Green
    exit 0
} else {
    Write-Host "UI smoke test FAILED ($script:failures assertion failure(s))" -ForegroundColor Red
    exit 1
}
