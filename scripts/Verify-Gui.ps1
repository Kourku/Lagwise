<#
.SYNOPSIS
    Launches a published NetPulse.exe against an isolated data folder and drives the Game Monitor workflow through UI Automation.
.DESCRIPTION
    Requires an interactive desktop. Verifies: the window opens and stays responsive, the Game Monitor tab works,
    a game can be selected, monitoring can be started, a lag marker added, and the session saved to the data folder.
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
    Check 'Monitoring state is displayed' ($null -ne (Wait-For { All | Where-Object { $_.Current.Name -cmatch 'MONITORING$' } | Select-Object -First 1 } 10)) ''
    Start-Sleep -Seconds 6
    Click-Element (Find-Named 'Mark lag' 'ControlType.Button')
    Check 'Lag marker accepted' ($null -ne (Wait-For { Find-Like 'Lag marked' } 5)) ''
    Check 'Reference measurements are labelled as not game-server ping' ($null -ne (Find-Like 'REFERENCE MEASUREMENTS \(NOT GAME-SERVER PING\)')) ''

    Click-Element (Find-Named 'Stop & save' 'ControlType.Button')
    $saved = Wait-For { Find-Like '^Session saved:' } 10
    Check 'Session saved message shows the file location' ($null -ne $saved) $(if ($saved) { ($saved.Current.Name -split "`n")[0] })

    $file = Get-ChildItem (Join-Path $DataDir 'sessions') -Filter '*.session.json' -ErrorAction SilentlyContinue | Select-Object -First 1
    Check 'Session file exists under the data folder' ($null -ne $file) $(if ($file) { $file.FullName })
    if ($file) {
        $j = Get-Content $file.FullName -Raw | ConvertFrom-Json
        Check 'Session is complete with samples and one lag marker' ($j.isComplete -and $j.samples.Count -gt 0 -and $j.lagMarkers.Count -eq 1) "samples=$($j.samples.Count)"
        Check 'Session records game, support status and limitations' ($j.gameDisplayName -eq 'League of Legends' -and $j.gameSupportStatus -eq 'ReferenceOnly' -and $j.limitations.Count -gt 0) "$($j.gameDisplayName) / $($j.gameSupportStatus)"
    }

    # ---- History & reports: the recording just saved must be listed and open ----
    Click-Element (Find-Like '^History & reports$')
    $row = Wait-For { All | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.DataItem' } | Select-Object -First 1 } 10
    Check 'History lists the saved recording' ($null -ne $row) ''
    if ($row) {
        Click-Element $row
        Check 'Selecting a recording shows its report actions' ($null -ne (Wait-For { Find-Named 'Export report' 'ControlType.Button' | Where-Object { $_.Current.IsEnabled } } 10)) ''
        Check 'Selecting a recording shows its summary and graph sections' (($null -ne (Find-Named 'Graph' 'ControlType.Text')) -and ($null -ne (Find-Like '^Moments you marked as laggy$'))) ''
    }

    # ---- Settings: change a value, save, and confirm it was applied and written ----
    Click-Element (Find-Like '^Settings$')
    $combo2 = All | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ComboBox' } | Select-Object -First 1
    Check 'Settings screen shows the test frequency selector' ($null -ne $combo2) ''
    if ($combo2) {
        ($combo2.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
        Start-Sleep -Milliseconds 700
        $two = All | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and $_.Current.Name -match 'Every 2 s' } | Select-Object -First 1
        Click-Element $two
        $save = Find-Named 'Save settings' 'ControlType.Button'
        Check 'Changing a setting enables Save' ($save.Current.IsEnabled) ''
        ($save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Check 'Saving confirms the change is in effect' ($null -ne (Wait-For { Find-Like '^Settings saved' } 8)) ''
        Check 'The footer reflects the new probe interval' ($null -ne (Wait-For { Find-Like 'Probing every 2 s' } 5)) ''
    }

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
