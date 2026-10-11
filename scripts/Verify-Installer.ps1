<#
.SYNOPSIS
    Installs the built NetPulse-Setup.exe into a temporary folder through its UI, runs the installed exe's self-test, then uninstalls.
.DESCRIPTION
    Uses UI Automation (needs an interactive desktop). No Desktop/Start Menu shortcuts are created and the app is not launched.
    It writes and then removes HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\NetPulse, so it refuses to run if a
    real NetPulse installation is already registered (it would overwrite it).
    Exit code 0 = install and uninstall both worked.
#>
param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$WorkDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$regPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NetPulse'
$checks = New-Object System.Collections.ArrayList
function Check($name, $ok, $detail) {
    [void]$checks.Add([pscustomobject]@{ Name = $name; Passed = [bool]$ok })
    Write-Host ("  [{0}] {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($detail) { "- $detail" }))
}

if (Test-Path $regPath) {
    $existing = (Get-ItemProperty $regPath).InstallLocation
    Write-Host "A NetPulse installation is already registered at '$existing'. Skipping installer verification so it is not overwritten." -ForegroundColor Yellow
    exit 2
}

$installDir = Join-Path $WorkDir 'installed'
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

function Get-Window($proc, $seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { return $AE::FromHandle($proc.MainWindowHandle) }
        Start-Sleep -Milliseconds 400
    }
}
function By-Id($win, $id) { $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function Text-Of($el) { $el.Current.Name }

try {
    # ---- install ----
    $p = Start-Process -FilePath $Setup -PassThru
    $win = Get-Window $p
    Check 'Installer window opens' ($null -ne $win) ''
    ($(By-Id $win 'TxtInstallDir').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($installDir)
    foreach ($id in 'ChkDesktopShortcut', 'ChkStartMenuShortcut', 'ChkLaunchAfter') {
        $t = (By-Id $win $id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($t.Current.ToggleState -eq 'On') { $t.Toggle() }
    }
    ((By-Id $win 'BtnAction').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()

    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and (Text-Of (By-Id $win 'BtnAction')) -ne 'Finished') { Start-Sleep -Milliseconds 500 }
    Check 'Installer reports completion' ((Text-Of (By-Id $win 'BtnAction')) -eq 'Finished') ''
    ((By-Id $win 'BtnAction').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    $null = $p.WaitForExit(10000)

    $exe = Join-Path $installDir 'NetPulse.exe'
    Check 'NetPulse.exe, app.ico and the setup copy were installed' ((Test-Path $exe) -and (Test-Path "$installDir\app.ico") -and (Test-Path "$installDir\NetPulse-Setup.exe")) $installDir
    Check 'Uninstall entry registered with the product version' ((Test-Path $regPath) -and ((Get-ItemProperty $regPath).DisplayVersion -eq ((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0])) $(if (Test-Path $regPath) { (Get-ItemProperty $regPath).DisplayVersion })

    # ---- installed copy works ----
    $report = Join-Path $WorkDir 'installed-selftest.json'
    $sp = Start-Process -FilePath $exe -ArgumentList '--selftest', '--selftest-out', "`"$report`"", '--selftest-data', "`"$(Join-Path $WorkDir 'selftest-data')`"" -PassThru
    Check 'Installed exe self-test completes' ($sp.WaitForExit(120000) -and $sp.ExitCode -eq 0) "exit $($sp.ExitCode)"

    # ---- uninstall ----
    $up = Start-Process -FilePath "$installDir\NetPulse-Setup.exe" -ArgumentList '--uninstall' -PassThru
    $uwin = Get-Window $up
    ((By-Id $uwin 'BtnAction').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()

    # the uninstaller shows a confirmation message box
    $deadline = (Get-Date).AddSeconds(30)
    $ok = $null
    while ((Get-Date) -lt $deadline -and -not $ok) {
        $box = $AE::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'NetPulse Uninstall')))
        if ($box) { $ok = $box.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'OK'))) }
        Start-Sleep -Milliseconds 400
    }
    if ($ok) { ($ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
    $null = $up.WaitForExit(15000)
    Start-Sleep -Seconds 5   # the cleanup command waits 2 s before deleting files

    Check 'Uninstall entry removed' (-not (Test-Path $regPath)) ''
    Check 'Application files removed' (-not (Test-Path $exe)) ''
}
catch {
    Check 'Installer verification completed without an unexpected error' $false $_.Exception.Message
}
finally {
    Get-Process -Name 'NetPulse-Setup' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if (Test-Path $regPath) { Remove-Item $regPath -Recurse -Force }   # never leave a test registration behind
}

$failed = @($checks | Where-Object { -not $_.Passed })
if ($failed.Count -gt 0) { Write-Host "$($failed.Count) installer check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "All $($checks.Count) installer checks passed." -ForegroundColor Green
exit 0
