<#
.SYNOPSIS
    Launches a published NetPulse.exe against an isolated data folder and drives the Game Monitor workflow through UI Automation.
.DESCRIPTION
    Requires an interactive desktop. Verifies: the window opens and stays responsive, the live graph is shown, the Game Monitor
    tab works, a game can be selected, monitoring can be started, lag can be marked with the button and with the system-wide
    shortcut, the session is saved and opens in History, and a changed setting is applied and written.
    The process is started with NETPULSE_DATA_DIR pointing at -DataDir so the user's real settings and recordings are untouched.
    Exit code 0 = all checks passed.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$DataDir,
    [string]$Out
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System.Runtime.InteropServices;
public class NpMouse {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, int e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, int flags, int extra);
}
'@

$checks = New-Object System.Collections.ArrayList
function Check($name, $ok, $detail) {
    [void]$checks.Add([pscustomobject]@{ Name = $name; Passed = [bool]$ok; Detail = "$detail" })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1} {2}" -f $mark, $name, $(if ($detail) { "- $detail" }))
}

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
$env:NETPULSE_DATA_DIR = $DataDir
$proc = Start-Process -FilePath $Exe -PassThru
$script:Root = $null

function Get-Win {
    $proc.Refresh()
    if ($proc.HasExited -or $proc.MainWindowHandle -eq [IntPtr]::Zero) { return $null }
    [NpMouse]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
    [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
}
function All { $w = Get-Win; if ($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) } else { @() } }
function Find-Named($name, $type) { All | Where-Object { $_.Current.Name -eq $name -and (-not $type -or $_.Current.ControlType.ProgrammaticName -eq $type) } | Select-Object -First 1 }
function Find-Like($pattern) { All | Where-Object { $_.Current.Name -match $pattern } | Select-Object -First 1 }
function Click-Element($el) {
    $r = $el.Current.BoundingRectangle
    [NpMouse]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
    Start-Sleep -Milliseconds 200
    [NpMouse]::mouse_event(2, 0, 0, 0, 0); [NpMouse]::mouse_event(4, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 900
}
function Wait-For($scriptBlock, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { $v = & $scriptBlock; if ($v) { return $v }; Start-Sleep -Milliseconds 500 }
    return $null
}

try {
    $win = Wait-For { Get-Win } 40
    Check 'Main window opens' ($null -ne $win) $(if ($win) { $win.Current.Name })
    if (-not $win) { throw 'no window' }

    Start-Sleep -Seconds 4
    Check 'Application stays responsive' ((Get-Process -Id $proc.Id).Responding) ''
    Check 'Game Monitor navigation entry exists' ($null -ne (Find-Like 'Game Monitor')) ''
    # first run: the "how much detail?" card is offered; choose Beginner
    $choose = Wait-For { Find-Named 'Choose Beginner' 'ControlType.Button' } 5
    Check 'First run offers the Beginner / Advanced choice' ($null -ne $choose) ''
    if ($choose) { ($choose.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 600 }
    Check 'Beginner hides the Advanced-only pages' (($null -eq (Find-Like '^Probe log$')) -and ($null -eq (Find-Like '^Route trace$'))) ''
    Check 'Overview shows the live ping graph' ($null -ne (Wait-For { Find-Like '^Live ping graph$' } 5)) ''
    # theme switch (title bar) applies and is saved; the mini overlay opens as its own window
    $toggle = Find-Named 'Switch theme' 'ControlType.Button'
    if ($toggle) { ($toggle.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 800 }
    Check 'The theme switch applies and saves the light theme' ((Get-Process -Id $proc.Id).Responding -and ((Get-Content (Join-Path $DataDir 'settings.json') -Raw -ErrorAction SilentlyContinue) -match '"Theme": "Light"')) ''
    if ($toggle) { ($toggle.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 800 }
    $ovBtn = Find-Named 'Mini overlay' 'ControlType.Button'
    if ($ovBtn) { ($ovBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
    $overlay = Wait-For { [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'NetPulse overlay'))) } 5
    Check 'The mini overlay opens' ($null -ne $overlay) ''
    if ($ovBtn) { ($ovBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 500 }
    Check 'Overview shows the Ready to play checklist' (($null -ne (Find-Like '^Ready to play\?$')) -and ($null -ne (Wait-For { Find-Like '^Ping stability$' } 10))) ''

    Click-Element (Find-Like 'Game Monitor')
    Check 'Game Monitor tab shows its heading' ($null -ne (Wait-For { Find-Named 'Game Monitor' 'ControlType.Text' } 5)) ''

    $combo = All | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ComboBox' } | Select-Object -First 1
    Check 'Game selector present' ($null -ne $combo) ''
    ($combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
    Start-Sleep -Milliseconds 800
    $items = All | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' }
    Check 'All seven game profiles are listed' ($items.Count -ge 7) "$($items.Count) entries"
    Check 'No profile claims to be Supported' (-not ($items | Where-Object { $_.Current.Name -match '\(Supported\)' })) ''
    $lol = $items | Where-Object { $_.Current.Name -match 'League of Legends' } | Select-Object -First 1
    Click-Element $lol

    $status = Wait-For { Find-Like 'League of Legends (is running|is not running|support programs|process named)' } 15
    Check 'Process status for the selected game is shown' ($null -ne $status) $(if ($status) { $status.Current.Name })

    $start = Find-Named 'Start monitoring' 'ControlType.Button'
    Check 'Start monitoring is enabled' ($start.Current.IsEnabled) ''
    Click-Element $start
    Check 'Monitoring state is displayed' ($null -ne (Wait-For { All | Where-Object { $_.Current.Name -cmatch 'RECORDING$' } | Select-Object -First 1 } 10)) ''
    Start-Sleep -Seconds 6
    Click-Element (Find-Named 'Mark lag' 'ControlType.Button')
    Check 'Lag marker accepted' ($null -ne (Wait-For { Find-Like 'Lag marked' } 5)) ''

    # the default system-wide shortcut (Ctrl+Shift+F9) must mark lag too
    [NpMouse]::keybd_event(0x11, 0, 0, 0); [NpMouse]::keybd_event(0x10, 0, 0, 0); [NpMouse]::keybd_event(0x78, 0, 0, 0)
    Start-Sleep -Milliseconds 80
    [NpMouse]::keybd_event(0x78, 0, 2, 0); [NpMouse]::keybd_event(0x10, 0, 2, 0); [NpMouse]::keybd_event(0x11, 0, 2, 0)
    Check 'The Mark lag shortcut adds a marker' ($null -ne (Wait-For { Find-Like 'Marked with the keyboard shortcut' } 5)) ''
    Check 'The footer shows what is being recorded' ($null -ne (Find-Like '^Recording League of Legends')) ''
    Check 'Beginner Game Monitor summarises the connections' ($null -ne (Find-Like '^What the game is connected to$')) ''

    Click-Element (Find-Named 'Stop & save' 'ControlType.Button')
    $saved = Wait-For { Find-Like '^Recording saved:' } 10
    Check 'Session saved message shows the file location' ($null -ne $saved) $(if ($saved) { ($saved.Current.Name -split "`n")[0] })

    $file = Get-ChildItem (Join-Path $DataDir 'sessions') -Filter '*.session.json' -ErrorAction SilentlyContinue | Select-Object -First 1
    Check 'Session file exists under the data folder' ($null -ne $file) $(if ($file) { $file.FullName })
    if ($file) {
        $j = Get-Content $file.FullName -Raw | ConvertFrom-Json
        Check 'Session is complete with samples and both lag markers' ($j.isComplete -and $j.samples.Count -gt 0 -and $j.lagMarkers.Count -eq 2) "samples=$($j.samples.Count), markers=$($j.lagMarkers.Count)"
        Check 'Session records game, support status and limitations' ($j.gameDisplayName -eq 'League of Legends' -and $j.gameSupportStatus -eq 'ReferenceOnly' -and $j.limitations.Count -gt 0) "$($j.gameDisplayName) / $($j.gameSupportStatus)"
    }

    # ---- History & reports: the recording just saved opens straight from the Game Monitor ----
    $open = Find-Named 'Open this recording' 'ControlType.Button'
    Check 'Open this recording is offered after saving' ($null -ne $open -and $open.Current.IsEnabled) ''
    if ($open) { ($open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
    $row = Wait-For { All | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.DataItem' } | Select-Object -First 1 } 10
    Check 'History lists the saved recording' ($null -ne $row) ''
    # wait for the recording itself (the top bar also has an "Export report" button, so wait for detail-only elements)
    Check 'The recording opens with its summary and graph sections' (($null -ne (Wait-For { Find-Named 'Graph' 'ControlType.Text' } 15)) -and ($null -ne (Find-Like '^Moments you marked as laggy$'))) ''
    Check 'The recording offers its report and CSV export' ($null -ne (Wait-For { Find-Named 'Export probe data (CSV)' 'ControlType.Button' | Where-Object { $_.Current.IsEnabled } } 5)) ''
    Check 'The recording graph is drawn' ($null -ne (Find-Like '^Recording graph$')) ''
    Check 'Lag marked with the shortcut is listed in the report' ($null -ne (Wait-For { Find-Like 'Marked with the keyboard shortcut' } 5)) ''
    Check 'History shows how much space recordings use' ($null -ne (Find-Like '^1 recording, .* MB on this PC')) ''
    Check 'History writes its summary index' (Test-Path (Join-Path $DataDir 'sessions\index.json')) ''


    # ---- Settings: change a value, save, and confirm it was applied and written ----
    Click-Element (Find-Like '^Settings$')
    $combo2 = All | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ComboBox' -and $_.Current.Name -eq 'Test frequency' } | Select-Object -First 1
    Check 'Settings screen shows the test frequency selector' ($null -ne $combo2) ''
    if ($combo2) {
        # keyboard selection (the dropdown may sit below the visible part of the page): 1 s -> next option, 2 s
        Add-Type -AssemblyName System.Windows.Forms
        $combo2.SetFocus()
        Start-Sleep -Milliseconds 300
        [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
        Start-Sleep -Milliseconds 600
        $save = Find-Named 'Save settings' 'ControlType.Button'
        Check 'Changing a setting enables Save' ($save.Current.IsEnabled) ''
        ($save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Check 'Saving confirms the change is in effect' ($null -ne (Wait-For { Find-Like '^Settings saved' } 8)) ''
        Check 'The footer reflects the new probe interval' ($null -ne (Wait-For { Find-Like 'Probing every 2 s' } 5)) ''
    }

    # ---- Advanced: extra pages and statistics appear, and the live probe log streams measurements ----
    $adv = Find-Named 'Advanced' 'ControlType.Button'
    if ($adv) { ($adv.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 800 }
    $log = Wait-For { Find-Like '^Probe log$' } 5
    Check 'Advanced adds the Probe log and Route trace pages' (($null -ne $log) -and ($null -ne (Find-Like '^Route trace$'))) ''
    if ($log) {
        Click-Element $log
        Check 'The probe log streams raw measurements' ($null -ne (Wait-For { All | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.DataItem' } | Select-Object -First 1 } 10)) ''
    }
    Click-Element (Find-Like '^Overview$')
    Check 'Advanced Overview shows per-server statistics' ($null -ne (Wait-For { Find-Like '^Per-server statistics$' } 5)) ''
    $beg = Find-Named 'Beginner' 'ControlType.Button'
    if ($beg) { ($beg.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 600 }

    $settingsFile = Join-Path $DataDir 'settings.json'
    Check 'Settings were written to the data folder' ((Test-Path $settingsFile) -and ((Get-Content $settingsFile -Raw) -match '"LastGameId": "lol"' -and (Get-Content $settingsFile -Raw) -match '"MonitoringIntervalMs": 2000')) $settingsFile
    Check 'Application still responsive at the end' ((Get-Process -Id $proc.Id).Responding) ''
}
catch {
    Check 'Verification completed without an unexpected error' $false $_.Exception.Message
}
finally {
    if (-not $proc.HasExited) { $null = $proc.CloseMainWindow(); Start-Sleep -Seconds 2; if (-not $proc.HasExited) { $proc.Kill() } }
}

$failed = @($checks | Where-Object { -not $_.Passed })
if ($Out) { [pscustomobject]@{ Ok = ($failed.Count -eq 0); Checks = $checks } | ConvertTo-Json -Depth 4 | Set-Content $Out }
if ($failed.Count -gt 0) { Write-Host "$($failed.Count) GUI check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "All $($checks.Count) GUI checks passed." -ForegroundColor Green
exit 0
