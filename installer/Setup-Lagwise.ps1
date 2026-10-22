<#
.SYNOPSIS
    Lagwise One-Click Windows Installer Script
.DESCRIPTION
    Installs Lagwise self-contained application, creates desktop and start menu shortcuts,
    and registers uninstaller in Windows Add/Remove Programs.
#>

param(
    [string]$InstallPath = "$env:LOCALAPPDATA\Programs\Lagwise",
    [switch]$NoShortcuts,
    [switch]$Launch
)

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   Lagwise Diagnostics Installer" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$SourceExe = "$ScriptDir\..\dist\portable\Lagwise.exe"
if (-not (Test-Path $SourceExe)) {
    $SourceExe = "$ScriptDir\payload\Lagwise.exe"
}
if (-not (Test-Path $SourceExe)) {
    $SourceExe = "$ScriptDir\Lagwise.exe"
}

if (-not (Test-Path $SourceExe)) {
    Write-Error "Could not find Lagwise.exe binary. Build or copy it first."
    exit 1
}

Write-Host "[1/4] Creating installation directory at $InstallPath..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null

Write-Host "[2/4] Copying files..." -ForegroundColor Yellow
Copy-Item $SourceExe "$InstallPath\Lagwise.exe" -Force
$IconSource = "$ScriptDir\..\src\Lagwise.App\app.ico"
if (Test-Path $IconSource) {
    Copy-Item $IconSource "$InstallPath\app.ico" -Force
}

if (-not $NoShortcuts) {
    Write-Host "[3/4] Creating Desktop and Start Menu shortcuts..." -ForegroundColor Yellow
    $WshShell = New-Object -ComObject WScript.Shell

    # Desktop shortcut
    $DesktopPath = [Environment]::GetFolderPath("Desktop")
    $Shortcut = $WshShell.CreateShortcut("$DesktopPath\Lagwise.lnk")
    $Shortcut.TargetPath = "$InstallPath\Lagwise.exe"
    $Shortcut.WorkingDirectory = $InstallPath
    $Shortcut.Description = "Lagwise - Real-time Network Quality Diagnostics"
    if (Test-Path "$InstallPath\app.ico") {
        $Shortcut.IconLocation = "$InstallPath\app.ico"
    }
    $Shortcut.Save()

    # Start Menu shortcut
    $ProgramsPath = [Environment]::GetFolderPath("Programs")
    $ShortcutStart = $WshShell.CreateShortcut("$ProgramsPath\Lagwise.lnk")
    $ShortcutStart.TargetPath = "$InstallPath\Lagwise.exe"
    $ShortcutStart.WorkingDirectory = $InstallPath
    $ShortcutStart.Description = "Lagwise - Real-time Network Quality Diagnostics"
    if (Test-Path "$InstallPath\app.ico") {
        $ShortcutStart.IconLocation = "$InstallPath\app.ico"
    }
    $ShortcutStart.Save()
}

Write-Host "[4/4] Registering with Windows Add/Remove Programs..." -ForegroundColor Yellow
$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Lagwise"
New-Item -Path $UninstallKey -Force | Out-Null
Set-ItemProperty -Path $UninstallKey -Name "DisplayName" -Value "Lagwise Real-Time Network Quality & Path Diagnostics"
Set-ItemProperty -Path $UninstallKey -Name "DisplayVersion" -Value (("$((Get-Item "$InstallPath\Lagwise.exe").VersionInfo.ProductVersion)").Split('+')[0])
Set-ItemProperty -Path $UninstallKey -Name "Publisher" -Value "Lagwise Diagnostics"
Set-ItemProperty -Path $UninstallKey -Name "DisplayIcon" -Value "$InstallPath\Lagwise.exe,0"
Set-ItemProperty -Path $UninstallKey -Name "InstallLocation" -Value $InstallPath
Set-ItemProperty -Path $UninstallKey -Name "UninstallString" -Value "powershell.exe -ExecutionPolicy Bypass -File `"$InstallPath\Uninstall.ps1`""

# Create uninstaller script
$UninstallScript = @"
Write-Host "Uninstalling Lagwise..." -ForegroundColor Yellow
Remove-Item "$DesktopPath\Lagwise.lnk" -ErrorAction SilentlyContinue
Remove-Item "$ProgramsPath\Lagwise.lnk" -ErrorAction SilentlyContinue
Remove-Item -Path "$UninstallKey" -Recurse -ErrorAction SilentlyContinue
# remove only the files this installer created (user data in %LOCALAPPDATA%\Lagwise is left alone), then the folder if empty
Remove-Item "$InstallPath\Lagwise.exe","$InstallPath\app.ico","$InstallPath\Uninstall.ps1" -ErrorAction SilentlyContinue
Start-Process cmd.exe -ArgumentList '/c timeout /t 2 /nobreak > NUL & rmdir "$InstallPath"' -WindowStyle Hidden
Write-Host "Lagwise uninstalled successfully." -ForegroundColor Green
"@
Set-Content -Path "$InstallPath\Uninstall.ps1" -Value $UninstallScript

Write-Host "==========================================" -ForegroundColor Green
Write-Host " Installation Successful!" -ForegroundColor Green
Write-Host " Location: $InstallPath\Lagwise.exe" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green

if ($Launch) {
    Start-Process "$InstallPath\Lagwise.exe"
}
