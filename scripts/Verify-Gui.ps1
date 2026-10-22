<#
.SYNOPSIS
    Launches a published Lagwise.exe against an isolated data folder and drives it through UI Automation.
.DESCRIPTION
    Requires an interactive desktop. Verifies: the window opens and stays responsive, the live graph is shown, and the whole
    Game Monitor workflow with a stand-in game (tests/Lagwise.FakeGame, added as a game the way a user adds one): Lagwise
    finds it, records automatically when it starts, takes lag marks by button and by the system-wide shortcut, saves when it
    closes, shows the session's verdict, and opens it in History. Then the other pages, and a changed setting is applied and written.
    The process is started with LAGWISE_DATA_DIR pointing at -DataDir so the user's real settings and recordings are untouched.
    Exit code 0 = all checks passed.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$DataDir,
    [string]$Out,
    # the stand-in game (StandInGame.exe from tests/Lagwise.FakeGame)
    [string]$FakeGame
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
$env:LAGWISE_DATA_DIR = $DataDir
# the stand-in game is added the way a user adds a game; automatic recording is on (the default)
if (-not $FakeGame) { $FakeGame = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\tests\Lagwise.FakeGame\bin\Release\net10.0\StandInGame.exe' }
$FakeGame = [System.IO.Path]::GetFullPath($FakeGame)
if (-not (Test-Path $FakeGame)) { Write-Host "Stand-in game not found: $FakeGame" -ForegroundColor Red; exit 1 }
[pscustomobject]@{ UserGames = @($FakeGame); AutoRecordGame = $true } | ConvertTo-Json | Set-Content -Path (Join-Path $DataDir 'settings.json') -Encoding UTF8
$game = $null
# results "from earlier runs": the Speed test and Bufferbloat pages must show them after a start
@(
    @{ T = '2026-10-10T10:00:00Z'; Kind = 'Speed'; Connection = 'Wired'; DownMbps = 400; UpMbps = 40 },
    @{ T = '2026-10-11T11:00:00Z'; Kind = 'Speed'; Connection = 'Wired'; DownMbps = 420; UpMbps = 41 },
    @{ T = '2026-10-10T19:30:00Z'; Kind = 'Speed'; Connection = 'Wired'; DownMbps = 210; UpMbps = 38 },
    @{ T = '2026-10-11T20:00:00Z'; Kind = 'Speed'; Connection = 'Wired'; DownMbps = 230; UpMbps = 39 },
    @{ T = '2026-10-12T20:00:00Z'; Kind = 'Bufferbloat'; Connection = 'Wired'; Grade = 'C'; RestMs = 14; DownDeltaMs = 45; UpDeltaMs = 12; DownMbps = 380; UpMbps = 38 },
    @{ T = '2026-10-13T21:00:00Z'; Kind = 'RealLoad'; Connection = 'Wired'; RestMs = 14; LoadedMs = 52; RiseMs = 38; DownMbps = 85; UpMbps = 2; PeakDownMbps = 95; PeakUpMbps = 3; Seconds = 130 }
) | ConvertTo-Json | Set-Content -Path (Join-Path $DataDir 'test-history.json') -Encoding UTF8
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
    # one Lagwise per data folder: a second start hands over to this one and closes
    $second = Start-Process -FilePath $Exe -PassThru
    Check 'A second start hands over to the running Lagwise instead of running twice' ($second.WaitForExit(10000) -and $second.ExitCode -eq 0 -and -not $proc.HasExited) ''
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
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Lagwise overlay'))) } 5
    Check 'The mini overlay opens' ($null -ne $overlay) ''
    if ($ovBtn) { ($ovBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 500 }
    Check 'Overview shows the Ready to play checklist' (($null -ne (Find-Like '^Ready to play\?$')) -and ($null -ne (Wait-For { Find-Like '^Ping stability$' } 10))) ''

    # Copy summary: plain text on the clipboard, without IP addresses (the user's clipboard is restored afterwards)
    $prevClip = $null; try { $prevClip = Get-Clipboard -Raw } catch { }
    $copy = Find-Named 'Copy summary' 'ControlType.Button'
    if ($copy) { ($copy.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 700 }
    $clip = ''; try { $clip = [string](Get-Clipboard -Raw) } catch { }
    # public reference servers may appear by name ("Cloudflare (1.1.1.1)"); the PC's own and its router's private addresses must not
    Check 'Copy summary puts a connection summary on the clipboard' (($clip -match 'connection check') -and ($clip -match 'does not measure game-server ping') -and ($clip -notmatch '192\.168\.|(^|[^\d])10\.\d+\.\d+\.\d+|172\.(1[6-9]|2\d|3[01])\.')) (($clip -split "`n")[0])
    if ($prevClip) { try { Set-Clipboard -Value $prevClip } catch { } }

    # Export report (Ctrl+E) into the test data folder: the file is written and the result shows as a notification
    Add-Type -AssemblyName System.Windows.Forms
    $reportPath = Join-Path $DataDir 'export\report.md'
    New-Item -ItemType Directory -Force -Path (Split-Path $reportPath) | Out-Null
    [NpMouse]::keybd_event(0x11, 0, 0, 0); [NpMouse]::keybd_event(0x45, 0, 0, 0)
    Start-Sleep -Milliseconds 60
    [NpMouse]::keybd_event(0x45, 0, 2, 0); [NpMouse]::keybd_event(0x11, 0, 2, 0)
    Start-Sleep -Seconds 2
    [System.Windows.Forms.SendKeys]::SendWait($reportPath); Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Check 'Export report saves the file and confirms it in a notification' (($null -ne (Wait-For { if (Test-Path $reportPath) { $reportPath } } 8)) -and ($null -ne (Wait-For { Find-Like '^Report saved: report\.md$' } 5))) ''

    Click-Element (Find-Like 'Game Monitor')
    Check 'Game Monitor tab shows its heading' ($null -ne (Wait-For { Find-Named 'Game Monitor' 'ControlType.Text' } 5)) ''

    # the game list: all games, plus the stand-in game added in Settings (and whatever Windows lists on this PC)
    $combo = Find-Named 'Watch for' 'ControlType.ComboBox'
    Check 'Game list present' ($null -ne $combo) ''
    if ($combo) { ($combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand() }
    Start-Sleep -Milliseconds 800
    $items = All | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' }
    $names = ($items | ForEach-Object { $_.Current.Name }) -join ', '
    Check 'The game list offers all games and the added game' ((Find-Like '^All my games$') -and (Find-Like '^Stand-in Game$')) "$($items.Count) entries"
    if ($combo) { ($combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse() }
    Start-Sleep -Milliseconds 400
    Check 'With no game running, the page says so' ($null -ne (Wait-For { Find-Like '^No game running$' } 10)) ''
    Check 'The live connection tiles are shown' (($null -ne (Find-Like '^Router\.')) -and ($null -ne (Find-Like '^Processor\.'))) ''

    # the game starts: Lagwise finds it by its file and records by itself
    $game = Start-Process -FilePath $FakeGame -ArgumentList '300' -PassThru
    Check 'Starting the game starts a recording by itself' ($null -ne (Wait-For { Find-Like '^Recording Stand-in Game$' } 20)) ''
    Check 'Monitoring state is displayed' ($null -ne (Wait-For { All | Where-Object { $_.Current.Name -cmatch 'RECORDING$' } | Select-Object -First 1 } 10)) ''
    Start-Sleep -Seconds 6
    Click-Element (Find-Named 'Mark lag' 'ControlType.Button')
    Check 'Lag marker accepted' ($null -ne (Wait-For { Find-Like '^Lag marked at' } 5)) ''

    # the default system-wide shortcut (Ctrl+Shift+F9) must mark lag too
    [NpMouse]::keybd_event(0x11, 0, 0, 0); [NpMouse]::keybd_event(0x10, 0, 0, 0); [NpMouse]::keybd_event(0x78, 0, 0, 0)
    Start-Sleep -Milliseconds 80
    [NpMouse]::keybd_event(0x78, 0, 2, 0); [NpMouse]::keybd_event(0x10, 0, 2, 0); [NpMouse]::keybd_event(0x11, 0, 2, 0)
    Check 'The Mark lag shortcut adds a marker' ($null -ne (Wait-For { Find-Like 'Marked with the keyboard shortcut' } 5)) ''
    Check 'The footer shows what is being recorded' ($null -ne (Find-Like '^Recording Stand-in Game  ')) ''
    # the taskbar jump list's "Mark lag" starts Lagwise.exe --command mark-lag, which the running copy acts on
    $jump = Start-Process -FilePath $Exe -ArgumentList '--command', 'mark-lag' -PassThru
    Check 'The jump list task Mark lag reaches the running Lagwise' ($jump.WaitForExit(10000) -and $jump.ExitCode -eq 0) ''
    Start-Sleep -Seconds 1
    Check 'The session so far gets a verdict while recording' ($null -ne (Wait-For { Find-Like '^This session so far$' } 15)) ''

    # the game closes: the recording is saved by itself once the game has stayed closed for 20 s
    if ($game -and -not $game.HasExited) { $game.Kill(); $game.WaitForExit(5000) | Out-Null }
    $saved = Wait-For { Find-Like '^Recording saved:' } 45
    Check 'Closing the game saves the recording by itself' ($null -ne $saved) $(if ($saved) { $saved.Current.Name })
    Check 'The last session shows its verdict' ($null -ne (Wait-For { Find-Like '^Verdict: ' } 10)) $(if ($v = Find-Like '^Verdict: ') { $v.Current.Name })

    $file = Get-ChildItem (Join-Path $DataDir 'sessions') -Filter '*.session.json' -ErrorAction SilentlyContinue | Select-Object -First 1
    Check 'Session file exists under the data folder' ($null -ne $file) $(if ($file) { $file.FullName })
    if ($file) {
        $j = Get-Content $file.FullName -Raw | ConvertFrom-Json
        Check 'Session is complete with samples and all three lag markers (button, shortcut, jump list)' ($j.isComplete -and $j.samples.Count -gt 0 -and $j.lagMarkers.Count -eq 3) "samples=$($j.samples.Count), markers=$($j.lagMarkers.Count)"
        Check 'Session records the game, that it started by itself, and the game being online' ($j.gameDisplayName -eq 'Stand-in Game' -and $j.automatic -and $j.gameRecording -and @($j.connections | Where-Object { $_.protocol -eq 'UDP' }).Count -ge 1 -and $j.gameProcesses.Count -ge 1) "$($j.gameDisplayName), connections=$($j.connections.Count)"
        Check 'Session keeps this PC''s processor load with its traffic' (@($j.throughput | Where-Object { $null -ne $_.cpu }).Count -gt 0) ''
    }
    Check 'Recent game sessions lists it' ($null -ne (Wait-For { Find-Like '^Recent game sessions$' } 5)) ''

    # ---- History & reports: the recording just saved opens straight from the Game Monitor ----
    $open = Find-Named 'Open this recording' 'ControlType.Button'
    Check 'Open this recording is offered after saving' ($null -ne $open -and $open.Current.IsEnabled) ''
    if ($open) { ($open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
    $row = Wait-For { All | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.DataItem' } | Select-Object -First 1 } 10
    Check 'History lists the saved recording' ($null -ne $row) ''
    # wait for the recording itself (the top bar also has an "Export report" button, so wait for detail-only elements)
    Check 'The recording opens with its summary and graph sections' (($null -ne (Wait-For { Find-Named 'Graph' 'ControlType.Text' } 15)) -and ($null -ne (Find-Like '^Moments you marked as laggy$'))) ''
    Check 'The recording shows whether it was your connection' (($null -ne (Find-Like '^Was it your connection\?$')) -and ($null -ne (Find-Like '^Verdict: '))) ''
    Check 'The recording offers its report and CSV export' ($null -ne (Wait-For { Find-Named 'Export probe data (CSV)' 'ControlType.Button' | Where-Object { $_.Current.IsEnabled } } 5)) ''
    Check 'The recording graph is drawn' ($null -ne (Find-Like '^Recording graph$')) ''
    # the recording kept this PC's traffic: its "Network activity" view draws it
    $graphView = Find-Named 'Graph view' 'ControlType.ComboBox'
    if ($graphView) {
        Add-Type -AssemblyName System.Windows.Forms
        $graphView.SetFocus(); Start-Sleep -Milliseconds 300
        [System.Windows.Forms.SendKeys]::SendWait('{DOWN}{DOWN}{DOWN}'); Start-Sleep -Milliseconds 800
    }
    Check 'A recording shows its network activity view' ($null -ne (Wait-For { Find-Like '^Recording network activity graph$' } 5)) ''
    if ($graphView) { $graphView.SetFocus(); [System.Windows.Forms.SendKeys]::SendWait('{UP}{UP}{UP}'); Start-Sleep -Milliseconds 500 }
    Check 'Lag marked with the shortcut is listed in the report' ($null -ne (Wait-For { Find-Like 'Marked with the keyboard shortcut' } 5)) ''
    Check "History says whether this PC was busy when lag was marked" ($null -ne (Find-Like "^Lag and this PC's own traffic\. ")) ''
    Check 'History shows how much space recordings use' ($null -ne (Find-Like '^1 recording, .* MB on this PC')) ''
    Check 'History writes its summary index' (Test-Path (Join-Path $DataDir 'sessions\index.json')) ''

    # time-of-day patterns over all recordings
    $analyse = Find-Named 'Analyse my recordings' 'ControlType.Button'
    if ($analyse) { ($analyse.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
    Check 'History analyses recordings by time of day' (($null -ne (Wait-For { Find-Like '^1 recording, .* reference probes' } 15)) -and ($null -ne (Find-Like '^Time of day chart$'))) ''

    # keyboard: Ctrl+1 opens the first page of the sidebar (the Overview)
    [NpMouse]::keybd_event(0x11, 0, 0, 0); [NpMouse]::keybd_event(0x31, 0, 0, 0)
    Start-Sleep -Milliseconds 80
    [NpMouse]::keybd_event(0x31, 0, 2, 0); [NpMouse]::keybd_event(0x11, 0, 2, 0)
    Check 'Ctrl+1 opens the Overview' ($null -ne (Wait-For { Find-Like '^Ready to play\?$' } 5)) ''
    # the jump list's "History & reports" switches the open window to that page
    $jump = Start-Process -FilePath $Exe -ArgumentList '--command', 'history' -PassThru
    $null = $jump.WaitForExit(10000)
    $onHistory = Wait-For { Find-Like '^Analyse (my recordings|again)$' } 8
    Check 'The jump list task History opens that page in the running window' ($null -ne $onHistory) $(if (-not $onHistory) { "exit $($jump.ExitCode); on screen: " + ((All | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match '^(Ready to play\?|Recordings|Graph|Was it your connection\?|Analyse.*)$' }) -join ', ') })

    # Network activity: live adapter throughput with its graph
    Click-Element (Find-Like '^Network activity$')
    Check 'Network activity shows live speeds and the graph' (($null -ne (Wait-For { Find-Like '^Network activity graph$' } 5)) -and ($null -ne (Find-Like '^Download now$'))) ''
    Check 'Network activity lists the programs online' ($null -ne (Wait-For { Find-Like '^Programs online$' } 8)) ''

    # Site check: one site checked for real; any verdict will do, the page must reach one
    Click-Element (Find-Like '^Site check$')
    Check 'Site check lists the popular sites' (($null -ne (Wait-For { Find-Like '^YouTube$' } 5)) -and ($null -ne (Find-Like '^Reddit$'))) ''
    $again = Find-Named 'Check YouTube again' 'ControlType.Button'
    if ($again) { ($again.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
    $verdict = Wait-For { Find-Like '^(Up|Slow|Unreachable|Blocked by your DNS|Blocked on the way|Blocked on this PC|Intercepted|Legal block|Problem at the site|Name not found|No internet)$' } 30
    Check 'A site check reaches a verdict' ($null -ne $verdict) $(if ($verdict) { $verdict.Current.Name })

    # Security check: the local part shows at once; the network part runs on Check
    Click-Element (Find-Like '^Security check$')
    Check 'The security check reads this PC at once' (($null -ne (Wait-For { Find-Like '^Security verdict$' } 8)) -and ($null -ne (Find-Like '^Windows Firewall$'))) ''
    $sec = Find-Named 'Run the security check' 'ControlType.Button'
    if ($sec) { ($sec.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
    Check 'The security check finishes its network part' ($null -ne (Wait-For { Find-Like '^Checked \d\d:\d\d\.' } 45)) ''

    # Connection diary, and its 24-hour strip on the Overview
    Click-Element (Find-Like '^Connection diary$')
    Check 'The connection diary shows its hour-by-hour chart' (($null -ne (Wait-For { Find-Like '^Connection diary chart$' } 5)) -and ($null -ne (Find-Like '^Diary summary$'))) ''
    Click-Element (Find-Like '^Overview$')
    Check 'The Overview shows the last 24 hours' ($null -ne (Wait-For { Find-Like '^Last 24 hours chart$' } 5)) ''

    # stored results, seeded before the start
    Click-Element (Find-Like '^Speed test$')
    Check 'Speed test keeps results over time' (($null -ne (Wait-For { Find-Like '^Typical of your 4 tests' } 5)) -and ($null -ne (Find-Like '^Recent results$'))) ''
    Check 'Speed test shows the last stored result after a start' ($null -ne (Find-Like '^Last test .*230 Mbps down')) ''
    Click-Element (Find-Like '^Bufferbloat test$')
    Check 'Bufferbloat lists its tests and what was seen in real use' (($null -ne (Wait-For { Find-Like '^Seen in real use$' } 5)) -and ($null -ne (Find-Like 'Seen once in real use: ping rose by 38 ms'))) ''


    # ---- Settings: change a value, save, and confirm it was applied and written ----
    Click-Element (Find-Like '^Settings$')
    $combo2 = All | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ComboBox' -and $_.Current.Name -eq 'Test frequency' } | Select-Object -First 1
    Check 'Settings screen shows the test frequency selector' ($null -ne $combo2) ''
    Check 'Settings offers desktop alerts' ($null -ne (Find-Like '^Show desktop alerts$')) ''
    Check 'The Save bar stays hidden until something changes' ($null -eq (Find-Named 'Save settings' 'ControlType.Button')) ''
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

    # every page, opened with its Ctrl+number shortcut (Advanced shows all ten), must render without an interface error;
    # a broken template only fails when its page is first drawn, and is then written to logs\errors.log
    foreach ($vk in 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x30) {
        [NpMouse]::keybd_event(0x11, 0, 0, 0); [NpMouse]::keybd_event($vk, 0, 0, 0)
        Start-Sleep -Milliseconds 60
        [NpMouse]::keybd_event($vk, 0, 2, 0); [NpMouse]::keybd_event(0x11, 0, 2, 0)
        Start-Sleep -Milliseconds 900
    }
    $errLog = Join-Path $DataDir 'logs\errors.log'
    Check 'Every page opens without an interface error' (-not (Test-Path $errLog)) $(if (Test-Path $errLog) { (Get-Content $errLog -TotalCount 3) -join ' ' })
    Click-Element (Find-Like '^Overview$')
    $beg = Find-Named 'Beginner' 'ControlType.Button'
    if ($beg) { ($beg.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 600 }

    $settingsFile = Join-Path $DataDir 'settings.json'
    Check 'Settings were written to the data folder' ((Test-Path $settingsFile) -and ((Get-Content $settingsFile -Raw) -match 'StandInGame\.exe' -and (Get-Content $settingsFile -Raw) -match '"MonitoringIntervalMs": 2000')) $settingsFile
    Check 'Application still responsive at the end' ((Get-Process -Id $proc.Id).Responding) ''

    # the installer closes a running Lagwise this way: it saves and exits, rather than being killed
    $bye = Start-Process -FilePath $Exe -ArgumentList '--command', 'exit' -PassThru
    $null = $bye.WaitForExit(10000)
    Check 'Asked to exit by a second start (as the installer does), Lagwise saves and closes' ($proc.WaitForExit(20000) -and $proc.ExitCode -eq 0) ''
}
catch {
    Check 'Verification completed without an unexpected error' $false $_.Exception.Message
}
finally {
    if ($game -and -not $game.HasExited) { $game.Kill() }
    if (-not $proc.HasExited) { $null = $proc.CloseMainWindow(); Start-Sleep -Seconds 2; if (-not $proc.HasExited) { $proc.Kill() } }
}

$failed = @($checks | Where-Object { -not $_.Passed })
if ($Out) { [pscustomobject]@{ Ok = ($failed.Count -eq 0); Checks = $checks } | ConvertTo-Json -Depth 4 | Set-Content $Out }
if ($failed.Count -gt 0) { Write-Host "$($failed.Count) GUI check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "All $($checks.Count) GUI checks passed." -ForegroundColor Green
exit 0
