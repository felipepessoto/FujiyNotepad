#requires -Version 7.0
<#
.SYNOPSIS
    App-layer UI test for FujiyNotepad, driven by the Windows App Development CLI (winapp ui).

.DESCRIPTION
    Launches the *published* WinUI app on a sample file and drives it through Windows UI Automation to
    assert the app-layer glue works end to end: the XAML loads, the window and menus render, the file opens
    and the status bar populates, and each feature's menu (encoding, zoom, find, filter, bookmarks, theme,
    font, dialogs, ...) works without crashing. This is the one layer the FujiyNotepad.Core /
    FujiyNotepad.Presentation unit tests cannot cover (it needs a real desktop session and a built app), so it
    runs as a dedicated CI job rather than via `dotnet test`.

    It targets the published Native-AOT executable on purpose: this app's nastiest regressions (e.g. the
    CsWinRT file-picker CCW failure, missing resources.pri) only reproduce in the AOT build, so a UI test
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

# The test drives the real app, which persists every settings change (theme, font, tab width, the View
# toggles, ...) to its settings.json — that would clobber the developer's saved settings on a local run. Back
# the file up now and restore it in Cleanup so a run is non-destructive. On a fresh CI runner there is no prior
# file, so settingsExisted is false and Cleanup just deletes the one the test created.
$script:settingsPath = Join-Path $env:LOCALAPPDATA 'FujiyNotepad\settings.json'
$script:settingsExisted = Test-Path $script:settingsPath
$script:settingsBackup = $null
if ($script:settingsExisted) {
    $script:settingsBackup = [System.IO.Path]::GetTempFileName()
    Copy-Item $script:settingsPath $script:settingsBackup -Force
    Write-Host "Backed up settings.json (restored after the test)"
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
$sample = Join-Path ([System.IO.Path]::GetTempPath()) ("fujiy-uitests-{0}.txt" -f ([guid]::NewGuid().ToString('N')))
[System.IO.File]::WriteAllText($sample, ($sampleLines -join "`r`n"))
$sampleName = Split-Path $sample -Leaf

Write-Host "Launching $Exe on $sampleName"
$proc = Start-Process -FilePath $Exe -ArgumentList "`"$sample`"" -PassThru

function Cleanup {
    Get-Process FujiyNotepad* -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Remove-Item $sample -Force -ErrorAction SilentlyContinue

    # Restore the settings the app overwrote while the test drove it (done after killing the process so it can't
    # write again). Give the killed process a moment to release the file first.
    Start-Sleep -Milliseconds 400
    if ($script:settingsExisted) {
        if ($script:settingsBackup -and (Test-Path $script:settingsBackup)) {
            Copy-Item $script:settingsBackup $script:settingsPath -Force
            Remove-Item $script:settingsBackup -Force -ErrorAction SilentlyContinue
            Write-Host "Restored settings.json"
        }
    } else {
        Remove-Item $script:settingsPath -Force -ErrorAction SilentlyContinue
    }
}

$script:proc = $proc

# --- winapp ui helpers (UIA-driven, so they need no mouse/DPI and run unattended in CI) ---
function UiInvoke([string]$Selector, [int]$WaitMs = 600) {
    winapp ui invoke $Selector -w $script:hwnd 2>$null | Out-Null
    Start-Sleep -Milliseconds $WaitMs
}
function UiMenu([string]$Top, [string]$Item, [int]$WaitMs = 750) {
    winapp ui invoke $Top -w $script:hwnd 2>$null | Out-Null
    Start-Sleep -Milliseconds 450
    winapp ui invoke $Item -w $script:hwnd 2>$null | Out-Null
    Start-Sleep -Milliseconds $WaitMs
}
function UiSubMenu([string]$Top, [string]$Sub, [string]$Item, [int]$WaitMs = 750) {
    winapp ui invoke $Top -w $script:hwnd 2>$null | Out-Null
    Start-Sleep -Milliseconds 400
    winapp ui invoke $Sub -w $script:hwnd 2>$null | Out-Null
    Start-Sleep -Milliseconds 400
    winapp ui invoke $Item -w $script:hwnd 2>$null | Out-Null
    Start-Sleep -Milliseconds $WaitMs
}
function UiValue([string]$Selector) {
    return ((winapp ui get-value $Selector -w $script:hwnd 2>$null | Out-String).Trim())
}
function UiHas([string]$Text) {
    return ((winapp ui search $Text -w $script:hwnd 2>$null | Out-String) -match 'Found\s+[1-9]')
}

try {
    $id = $proc.Id

    # 1) The process stays up (an AOT activation crash would exit within a couple of seconds).
    Start-Sleep -Seconds 6
    Assert (-not $proc.HasExited) "Process is still running after launch" `
        "Process exited with code $($proc.ExitCode) — likely an activation/AOT crash."
    if ($proc.HasExited) { throw "App exited on launch; aborting UI tests." }

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
    $script:hwnd = $hwnd

    # 3) The menu bar rendered with its top-level menus.
    $menuText = (winapp ui inspect window -w $hwnd 2>$null | Out-String)
    foreach ($m in 'File', 'Edit', 'View', 'Encoding', 'Help') {
        Assert ($menuText -match "\b$m\b") "Menu '$m' is present"
    }

    # 4) The status bar populated from the opened file (line + character counts).
    $status = (winapp ui get-value 'LblStatus' -w $hwnd 2>$null | Out-String)
    Assert ($status -match '5\s+lines') "Status bar shows the line count (5 lines)" "Status was: '$($status.Trim())'"

    # 5) Status bar populated from the opened file: the device-free formatters (StatusText, CharacterCounter,
    #    LineEndingDetector, EncodingDetector) wired through to the real labels.
    $charCount = UiValue 'LblCharCount'
    Assert ($charCount -match '\d[\d,]*\s+character') "Status bar shows a character count" "LblCharCount: '$charCount'"
    $lineEnding = UiValue 'LblLineEnding'
    Assert ($lineEnding -eq 'CRLF') "Status bar shows the CRLF line ending" "LblLineEnding: '$lineEnding'"
    $encoding = UiValue 'LblEncoding'
    Assert ($encoding -match 'UTF-8') "Status bar shows the detected encoding (UTF-8)" "LblEncoding: '$encoding'"
    $cursor = UiValue 'LblCursor'
    Assert ($cursor -match 'Ln\s*1,\s*Col\s*1') "Status bar shows the caret position" "LblCursor: '$cursor'"
    $zoom = UiValue 'LblZoom'
    Assert ($zoom -eq '100%') "Status bar shows 100% zoom" "LblZoom: '$zoom'"

    # 5b) Accessibility (#75): the custom TextCanvas AutomationPeer exposes the caret line's text to screen
    #     readers via the UIA Value pattern (the Win2D surface otherwise exposes no text content).
    $canvasText = UiValue 'TextCanvas'
    Assert ($canvasText -match 'alpha first line') `
        "TextCanvas exposes the caret line text to screen readers" "TextCanvas value: '$canvasText'"

    # 6) Encoding menu re-decodes the file; the status label follows the chosen / auto-detected encoding.
    UiMenu 'Encoding' 'UTF-16 LE'
    $enc16 = UiValue 'LblEncoding'
    Assert ($enc16 -match 'UTF-16') "Encoding menu switches the active encoding to UTF-16 LE" "LblEncoding: '$enc16'"
    UiMenu 'Encoding' 'Auto-detect'
    $encAuto = UiValue 'LblEncoding'
    Assert ($encAuto -match 'UTF-8') "Auto-detect restores the detected UTF-8 encoding" "LblEncoding: '$encAuto'"

    # 7) View > Zoom adjusts the zoom level shown in the status bar.
    UiMenu 'View' 'Zoom In'
    $zoomIn = UiValue 'LblZoom'
    $zoomInPct = 0; [void][int]::TryParse(($zoomIn -replace '%', ''), [ref]$zoomInPct)
    Assert ($zoomInPct -gt 100) "Zoom In increases the zoom level" "LblZoom: '$zoomIn'"
    UiMenu 'View' 'Reset Zoom'
    Assert ((UiValue 'LblZoom') -eq '100%') "Reset Zoom returns to 100%" "LblZoom: '$(UiValue 'LblZoom')'"

    # 8) View toggles (markers are Win2D-painted, so assert the handlers run without crashing).
    UiMenu 'View' 'Line Numbers';   Assert (-not $proc.HasExited) "Line Numbers toggle did not crash the app"
    UiMenu 'View' 'Show Whitespace'; Assert (-not $proc.HasExited) "Show Whitespace toggle did not crash the app"

    # 8a) Highlight Selection Occurrences (#130): the highlight is Win2D-painted from the current selection, so
    #     assert the toggle handler runs without crashing (the decision + render model are unit-tested).
    UiMenu 'View' 'Highlight Selection Occurrences'
    Assert (-not $proc.HasExited) "Highlight Selection toggle off did not crash"
    UiMenu 'View' 'Highlight Selection Occurrences'  # toggle back on
    Assert (-not $proc.HasExited) "Highlight Selection toggle on did not crash"

    # 8b) Word Wrap (#31): turning it on wraps long lines and hides the (now meaningless) horizontal scrollbar;
    #     turning it off restores it. The wrap layout math is unit-tested; here we assert the live wiring.
    UiMenu 'View' 'Word Wrap'
    Assert (-not $proc.HasExited) "Word Wrap toggle on did not crash"
    $hScrollHidden = ((winapp ui search 'HScroll' -w $hwnd 2>$null | Out-String) -notmatch 'Found\s+[1-9]')
    Assert $hScrollHidden "Word Wrap hides the horizontal scrollbar"
    UiMenu 'View' 'Word Wrap'  # toggle off
    Assert (-not $proc.HasExited) "Word Wrap toggle off did not crash"

    # 9) Menu reachability for features whose effect is Win2D-painted or clipboard-bound: invoke and assert
    #    the handler ran without crashing (a throwing handler would exit the AOT process).
    UiMenu 'Edit' 'Toggle Bookmark';        Assert (-not $proc.HasExited) "Toggle Bookmark did not crash"
    UiMenu 'Edit' 'Next Bookmark';          Assert (-not $proc.HasExited) "Next Bookmark did not crash"
    UiMenu 'Edit' 'Clear All Bookmarks';    Assert (-not $proc.HasExited) "Clear All Bookmarks did not crash"
    UiSubMenu 'Edit' 'Tab Width' '8';       Assert (-not $proc.HasExited) "Tab Width 8 did not crash"
    UiSubMenu 'View' 'Theme' 'Dark';        Assert (-not $proc.HasExited) "Theme Dark did not crash"
    UiSubMenu 'View' 'Theme' 'System';      Assert (-not $proc.HasExited) "Theme System did not crash"
    UiSubMenu 'View' 'Font' 'Cascadia Mono'; Assert (-not $proc.HasExited) "Font change did not crash"

    # 10) Dialogs open from their menu handlers and dismiss via the ContentDialog's CloseButton (its
    #     AutomationId — using the literal text 'Close' would collide with the title-bar close button).
    UiMenu 'Edit' 'Go To Line...' 900
    Assert (UiHas 'Go To Line') "Go To Line dialog opened"
    UiInvoke 'CloseButton'
    UiMenu 'Edit' 'Go To Percentage...' 900
    Assert (UiHas 'Go To Percentage') "Go To Percentage dialog opened"
    UiInvoke 'CloseButton'
    UiMenu 'View' 'Highlight Rules...' 900
    Assert (UiHas 'Colors') "Highlight Rules dialog opened"
    UiInvoke 'CloseButton'
    UiMenu 'Help' 'About FujiyNotepad...' 900
    Assert (UiHas 'Version') "About dialog opened"
    UiInvoke 'CloseButton'

    # 11) Find: open the bar, confirm its accessibility name, type a term, run Find Next, read the live count.
    UiMenu 'Edit' 'Find...'
    $nameJson = (winapp ui get-property 'MatchCaseToggle' -w $hwnd --property Name --json 2>$null | Out-String)
    $matchName = $null
    try { $matchName = ($nameJson | ConvertFrom-Json).properties.Name } catch { }
    Assert ($matchName -eq 'Match case') "Find bar option toggle exposes its accessibility name" "Name: '$matchName'"
    winapp ui set-value 'FindBox' 'ERROR' -w $hwnd 2>$null | Out-Null
    Start-Sleep -Milliseconds 700
    Assert ((UiValue 'FindBox') -match 'ERROR') "Find box accepts typed text"
    # Incremental find (issue #63): the match count appears as you type, before pressing Enter.
    $incCount = UiValue 'FindCount'
    Assert ($incCount -match '1 match') "Find counts the ERROR match incrementally, before Enter" "FindCount: '$incCount'"
    UiInvoke 'Find Next' 1100
    $findCount = UiValue 'FindCount'
    Assert ($findCount -match '1 match') "Find reports the single ERROR match in the sample" "FindCount: '$findCount'"
    Assert ((UiValue 'LblCursor') -match 'Ln\s*2') "Find Next moved the caret to the match line (line 2)" "LblCursor: '$(UiValue 'LblCursor')'"

    # 12) Filter / grep view: collapse the file to matching lines and read the filtered count.
    UiMenu 'Edit' 'Filter...'
    winapp ui set-value 'FilterBox' 'ERROR' -w $hwnd 2>$null | Out-Null
    Start-Sleep -Milliseconds 300
    UiInvoke 'Apply' 1200
    $filtered = UiValue 'LblStatus'
    Assert ($filtered -match 'Filtered:\s*1\s+of\s+5') "Filter shows 1 of 5 matching lines" "LblStatus: '$filtered'"

    # 12b) Clear Search History runs without crashing (sections 11-12 above populated the Find/Filter history).
    UiMenu 'Edit' 'Clear Search History'; Assert (-not $proc.HasExited) "Clear Search History did not crash"

    # 12c) Follow Tail (issue #28): enabling it tails appended lines live — the count grows and the status bar
    #      shows "Following", without pressing reload. The app shares the file ReadWrite so we can append to it.
    UiInvoke 'Clear filter and close'   # leave the filter view first; Follow Tail tails the full file
    Start-Sleep -Milliseconds 300
    UiMenu 'View' 'Follow Tail'
    Assert (-not $proc.HasExited) "Follow Tail toggle did not crash"
    [System.IO.File]::AppendAllText($sample, "`r`nzeta sixth line`r`neta seventh line")
    Start-Sleep -Milliseconds 2200   # > the ~1s poll + the resume-index + a status refresh tick
    $tailStatus = UiValue 'LblStatus'
    Assert ($tailStatus -match '7 lines') "Follow Tail picked up the 2 appended lines (7 lines total)" "LblStatus: '$tailStatus'"
    Assert ($tailStatus -match 'Following') "Follow Tail shows the 'Following' indicator" "LblStatus: '$tailStatus'"
    UiMenu 'View' 'Follow Tail'  # toggle off
    Assert (-not $proc.HasExited) "Follow Tail toggle off did not crash"

    # 13) Still alive after the full interaction sweep.
    Assert (-not $proc.HasExited) "Process is still running after the full interaction sweep"
}
catch {
    Write-Host "EXCEPTION: $_" -ForegroundColor Red
    $script:failures++
}
finally {
    # On failure, capture a screenshot for the CI artifact before tearing down.
    if ($script:failures -gt 0 -and $hwnd) {
        try {
            $shot = Join-Path $ArtifactDir 'ui-tests-failure.png'
            winapp ui screenshot window -w $hwnd --output $shot 2>$null | Out-Null
            Write-Host "Saved failure screenshot to $shot"
        } catch { }
    }
    Cleanup
}

Write-Host ''
if ($script:failures -eq 0) {
    Write-Host "UI tests PASSED" -ForegroundColor Green
    exit 0
} else {
    Write-Host "UI tests FAILED ($script:failures assertion failure(s))" -ForegroundColor Red
    exit 1
}
