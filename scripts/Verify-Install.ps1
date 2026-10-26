<#
.SYNOPSIS
    Checks the built-in install of a published Lagwise.exe: installs it into a temporary folder, runs the installed copy's
    self-test, then uninstalls it the way Settings > Apps does.
.DESCRIPTION
    Headless (no window, no mouse). It never touches the real Start menu, desktop or installation folder: it installs with
    --install-dir <temporary folder> --no-shortcuts. It does write and then remove
    HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Lagwise, so it refuses to run when a real Lagwise
    installation is registered (it would replace it). Exit code 0 = install and uninstall both worked, 2 = skipped.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$WorkDir
)

$ErrorActionPreference = 'Stop'
$regPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Lagwise'
$checks = New-Object System.Collections.ArrayList
function Check($name, $ok, $detail) {
    [void]$checks.Add([pscustomobject]@{ Name = $name; Passed = [bool]$ok })
    Write-Host ("  [{0}] {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($detail) { "- $detail" }))
}

if (Test-Path $regPath) {
    Write-Host "A Lagwise installation is registered at '$((Get-ItemProperty $regPath).InstallLocation)'. Skipping the install check so it is not replaced." -ForegroundColor Yellow
    exit 2
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$download = Join-Path $WorkDir 'download'
$installDir = Join-Path $WorkDir 'installed\Lagwise'
New-Item -ItemType Directory -Force -Path $download | Out-Null
Copy-Item $Exe (Join-Path $download 'Lagwise.exe') -Force
$portable = Join-Path $download 'Lagwise.exe'
$installed = Join-Path $installDir 'Lagwise.exe'
$version = ((Get-Item $Exe).VersionInfo.ProductVersion -split '\+')[0]

try {
    # ---- install --------------------------------------------------------------------------------------------------
    $p = Start-Process -FilePath $portable -ArgumentList '--install', '--quiet', '--no-shortcuts', '--install-dir', "`"$installDir`"" -PassThru -WindowStyle Hidden
    Check 'The downloaded copy installs itself (--install)' ($p.WaitForExit(60000) -and $p.ExitCode -eq 0) "exit $($p.ExitCode)"
    Check 'Lagwise.exe is in the installation folder, and nothing else' ((Test-Path $installed) -and @(Get-ChildItem $installDir).Count -eq 1) $installDir
    $reg = if (Test-Path $regPath) { Get-ItemProperty $regPath }
    Check 'Listed in Settings > Apps with its version' ($reg -and $reg.DisplayVersion -eq $version -and $reg.InstallLocation -eq $installDir) $(if ($reg) { "$($reg.DisplayName) $($reg.DisplayVersion)" })
    Check 'Settings > Apps uninstalls it with Lagwise.exe --uninstall' ($reg -and $reg.UninstallString -eq "`"$installed`" --uninstall") $(if ($reg) { $reg.UninstallString })

    # ---- the installed copy works -----------------------------------------------------------------------------------
    $report = Join-Path $WorkDir 'installed-selftest.json'
    $sp = Start-Process -FilePath $installed -ArgumentList '--selftest', '--selftest-out', "`"$report`"", '--selftest-data', "`"$(Join-Path $WorkDir 'selftest-data')`"" -PassThru
    Check 'The installed copy passes its self-test' ($sp.WaitForExit(120000) -and $sp.ExitCode -eq 0) "exit $($sp.ExitCode)"

    # ---- uninstall, as Settings > Apps does (quietly here) ---------------------------------------------------------
    $up = Start-Process -FilePath $installed -ArgumentList '--uninstall', '--quiet' -PassThru -WindowStyle Hidden
    Check 'Uninstall finishes (--uninstall)' ($up.WaitForExit(60000) -and $up.ExitCode -eq 0) "exit $($up.ExitCode)"
    Check 'The Settings > Apps entry is removed' (-not (Test-Path $regPath)) ''
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and (Test-Path $installDir)) { Start-Sleep -Milliseconds 500 }   # deleted a few seconds after it exits
    Check 'The program and its folder are removed' (-not (Test-Path $installDir)) ''
    Check 'The downloaded copy is untouched' (Test-Path $portable) ''
}
catch {
    Check 'Install check completed without an unexpected error' $false $_.Exception.Message
}
finally {
    if (Test-Path $regPath) { Remove-Item $regPath -Recurse -Force }   # never leave a test registration behind
}

$failed = @($checks | Where-Object { -not $_.Passed })
if ($failed.Count -gt 0) { Write-Host "$($failed.Count) install check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "All $($checks.Count) install checks passed." -ForegroundColor Green
exit 0
