<#
.SYNOPSIS
    Packs a published Lagwise.exe into an MSIX package, for the Microsoft Store or for a local test install.
.DESCRIPTION
    Takes the verified, self-contained Lagwise.exe from a build (default: dist/portable, written by build.ps1) and packs it
    with the manifest and logos in installer/store.

    Two modes, chosen by installer/store/identity.json:
      Store mode  - the three identity values from Partner Center are filled in. The package is NOT signed: the Microsoft Store
                    signs it on submission. Upload the .msix in Partner Center.
      Test mode   - identity.json is empty. The package gets the local test identity "Lagwise.LocalTest" and is signed with a
                    self-signed certificate (created in your user certificate store). Windows only installs it after that
                    certificate is trusted: see the note printed at the end.
    -Sign also signs a Store-mode package with a self-signed certificate whose subject is the Store publisher, for a local test
    of the exact Store identity (the Store itself ignores this signature).

    Needs the Windows SDK (makeappx, makepri, signtool). Nothing is installed and nothing outside dist/store is written.
.PARAMETER AppDir
    Folder containing the published Lagwise.exe.
.PARAMETER IdentityFile`n    Another identity file instead of installer/store/identity.json.`n.PARAMETER OutDir
    Where the .msix goes (default dist/store).
#>
[CmdletBinding()]
param(
    [string]$AppDir,
    [string]$OutDir,
    [string]$IdentityFile,
    [switch]$Sign
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
if (-not $AppDir) { $AppDir = Join-Path $Root 'dist\portable' }
if (-not $OutDir) { $OutDir = Join-Path $Root 'dist\store' }
$StoreDir = Join-Path $Root 'installer\store'

function Fail($text) { Write-Host "MSIX BUILD FAILED: $text" -ForegroundColor Red; exit 1 }
function Step($text) { Write-Host "== $text" -ForegroundColor Cyan }

# ---- inputs ---------------------------------------------------------------------------------------------------
$exe = Join-Path $AppDir 'Lagwise.exe'
if (-not (Test-Path $exe)) { Fail "no Lagwise.exe in $AppDir (run build.ps1 first, or pass -AppDir)" }

$props = [xml](Get-Content "$Root\Directory.Build.props" -Raw)
$productVersion = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ($productVersion -notmatch '^\d+\.\d+\.\d+$') { Fail "unexpected product version '$productVersion'" }
# the Store reserves the fourth number, so it is always 0
$packageVersion = "$productVersion.0"
$exeVersion = (Get-Item $exe).VersionInfo.ProductVersion
if (-not $exeVersion -or -not $exeVersion.StartsWith($productVersion)) { Fail "the exe is version '$exeVersion', not $productVersion" }

if (-not $IdentityFile) { $IdentityFile = Join-Path $StoreDir 'identity.json' }
$identity = Get-Content $IdentityFile -Raw | ConvertFrom-Json
$storeMode = [bool]($identity.PackageIdentityName -and $identity.Publisher -and $identity.PublisherDisplayName)
if ($storeMode) {
    if ($identity.Publisher -notmatch '^CN=') { Fail "Publisher must be the full 'CN=...' value from Partner Center" }
    if ($identity.PackageIdentityName -notmatch '^[A-Za-z0-9.\-]{3,50}$') { Fail "PackageIdentityName '$($identity.PackageIdentityName)' is not a valid package name" }
    $name = $identity.PackageIdentityName; $publisher = $identity.Publisher; $publisherDisplay = $identity.PublisherDisplayName
    $suffix = ''
}
else {
    $name = 'Lagwise.LocalTest'; $publisher = 'CN=Lagwise Local Test'; $publisherDisplay = 'Lagwise (local test)'
    $suffix = '-localtest'
    $Sign = $true
}
Write-Host ("Lagwise MSIX  version {0}  {1}  identity {2}" -f $packageVersion, $(if ($storeMode) { 'for the Microsoft Store' } else { 'local test package' }), $name) -ForegroundColor Cyan

# ---- tools ----------------------------------------------------------------------------------------------------
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$toolDir = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path "$($_.FullName)\x64\makeappx.exe" } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $toolDir) { Fail 'the Windows SDK (makeappx.exe) was not found under Windows Kits\10\bin' }
$bin = Join-Path $toolDir.FullName 'x64'
Write-Host "Windows SDK tools: $($toolDir.Name)"

# ---- stage ----------------------------------------------------------------------------------------------------
Step 'Staging the package contents'
$work = Join-Path ([IO.Path]::GetTempPath()) ("lagwise-msix-" + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'pkg'
New-Item -ItemType Directory -Force -Path $stage, $OutDir | Out-Null
try {
    Copy-Item $exe $stage
    Copy-Item (Join-Path $StoreDir 'Assets') (Join-Path $stage 'Assets') -Recurse

    $xml = Get-Content (Join-Path $StoreDir 'AppxManifest.template.xml') -Raw
    $xml = $xml.Replace('@IDENTITY_NAME@', [Security.SecurityElement]::Escape($name)).
        Replace('@PUBLISHER@', [Security.SecurityElement]::Escape($publisher)).
        Replace('@PUBLISHER_DISPLAY_NAME@', [Security.SecurityElement]::Escape($publisherDisplay)).
        Replace('@VERSION@', $packageVersion)
    if ($xml -match '@[A-Z_]+@') { Fail "an unfilled placeholder is left in the manifest: $($Matches[0])" }
    [void][xml]$xml   # well-formed?
    [IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $xml, (New-Object Text.UTF8Encoding($false)))

    # every file the manifest names must exist (a missing logo makes the Store reject the package)
    $manifest = [xml]$xml
    foreach ($attr in 'Square150x150Logo', 'Square44x44Logo') {
        $rel = $manifest.Package.Applications.Application.VisualElements.$attr
        if (-not (Test-Path (Join-Path $stage $rel))) { Fail "the manifest names $rel, which is not in Assets" }
    }
    foreach ($attr in 'Wide310x150Logo', 'Square71x71Logo', 'Square310x310Logo') {
        $rel = $manifest.Package.Applications.Application.VisualElements.DefaultTile.$attr
        if (-not (Test-Path (Join-Path $stage $rel))) { Fail "the manifest names $rel, which is not in Assets" }
    }
    if (-not (Test-Path (Join-Path $stage $manifest.Package.Properties.Logo))) { Fail 'the store logo is missing' }

    # ---- resource index (lets Windows pick the right logo size) ------------------------------------------------
    Step 'Building the resource index'
    $cfg = Join-Path $work 'priconfig.xml'
    & "$bin\makepri.exe" createconfig /cf $cfg /dq en-US /o | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'makepri createconfig failed' }
    & "$bin\makepri.exe" new /pr $stage /cf $cfg /mn (Join-Path $stage 'AppxManifest.xml') /of (Join-Path $stage 'resources.pri') /o | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'makepri new failed' }

    # ---- pack ---------------------------------------------------------------------------------------------------
    Step 'Packing'
    $msix = Join-Path $OutDir ("Lagwise-$packageVersion-x64$suffix.msix")
    if (Test-Path $msix) { Remove-Item $msix -Force }
    & "$bin\makeappx.exe" pack /d $stage /p $msix /o | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'makeappx pack failed' }

    # ---- sign (test packages, or -Sign) -------------------------------------------------------------------------
    $cer = $null
    if ($Sign) {
        Step 'Signing with a self-signed certificate (local test only)'
        $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
        if (-not $cert) {
            $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName 'Lagwise local test package' `
                -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(2)
        }
        $cer = Join-Path $OutDir ("Lagwise-local-test.cer")
        Export-Certificate -Cert $cert -FilePath $cer | Out-Null
        & "$bin\signtool.exe" sign /fd SHA256 /sha1 $cert.Thumbprint $msix | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail 'signtool failed' }
    }

    # ---- check ---------------------------------------------------------------------------------------------------
    Step 'Checking the package'
    $listing = Join-Path $work 'listing'
    & "$bin\makeappx.exe" unpack /p $msix /d $listing /o | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'the package could not be unpacked again' }
    foreach ($must in 'Lagwise.exe', 'AppxManifest.xml', 'resources.pri', 'Assets\StoreLogo.png') {
        if (-not (Test-Path (Join-Path $listing $must))) { Fail "the package lacks $must" }
    }
    $size = (Get-Item $msix).Length / 1MB
    $hash = (Get-FileHash $msix -Algorithm SHA256).Hash
    Write-Host ("`n{0}  ({1:N1} MB)`nSHA-256 {2}" -f $msix, $size, $hash) -ForegroundColor Green
    if ($storeMode) {
        Write-Host 'Unsigned on purpose: upload this file in Partner Center and the Store signs it.' -ForegroundColor Green
    }
    if ($cer) {
        Write-Host @"

To try the package on this PC (one time, in an ADMINISTRATOR PowerShell):
  Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople
then, as a normal user:
  Add-AppxPackage '$msix'
Remove it again with:
  Get-AppxPackage $name | Remove-AppxPackage
"@
    }
}
finally {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
