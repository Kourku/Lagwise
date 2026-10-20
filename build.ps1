<#
.SYNOPSIS
    Builds, verifies and packages NetPulse. Only a build that passes every check is promoted to dist/.
.DESCRIPTION
    1. Release build of the whole solution with warnings treated as errors.
    2. Full automated test suite.
    3. Publishes a self-contained win-x64 single-file portable build into a scratch folder (dist/_build/...), bundling the
       latest .NET 10 runtime patch named by Microsoft's release feed (a bundled runtime is not patched by Windows Update).
    4. Smoke-tests the PUBLISHED exe: headless self-test, then (unless -SkipGuiCheck) the Game Monitor workflow through UI Automation.
       Both run against an isolated data folder, never the user's real %LOCALAPPDATA%\NetPulse.
    5. Only then: writes dist/releases/<version>/ (zip, release notes, build-info.json, installer) and swaps dist/portable.
    If anything fails, dist/portable and dist/releases are left exactly as they were (the last known-good build).

    User data (settings, recordings) always lives in %LOCALAPPDATA%\NetPulse, never in dist/ or beside the exe.
.PARAMETER SkipGuiCheck
    Skip the UI Automation check (needed on machines without an interactive desktop). The headless self-test still runs.
.PARAMETER SkipInstaller
    Do not build the NetPulse-Setup.exe installer.
.PARAMETER VerifyInstaller
    Additionally install and uninstall the built installer into a temporary folder (UI Automation; no shortcuts are created).
.PARAMETER RuntimeVersion
    The .NET runtime patch to bundle (for example 10.0.13) when Microsoft's release feed cannot be reached. Without it
    the build asks the feed and fails if it cannot, rather than ship an outdated runtime silently.
#>
[CmdletBinding()]
param(
    [switch]$SkipGuiCheck,
    [switch]$SkipInstaller,
    [switch]$VerifyInstaller,
    [string]$RuntimeVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Root = $PSScriptRoot
Set-Location $Root

function Step($text)  { Write-Host "`n== $text" -ForegroundColor Cyan }
function Ok($text)    { Write-Host "   $text" -ForegroundColor Green }
function Fail($text)  {
    Write-Host "`nBUILD FAILED: $text" -ForegroundColor Red
    Write-Host "dist/portable and dist/releases were not modified. Scratch output: $Work" -ForegroundColor Yellow
    exit 1
}
function Get-Sha256($path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($path)
    try { ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') } finally { $stream.Dispose(); $sha.Dispose() }
}
function New-Zip($sourceDir, $zipPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($sourceDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
}
function Run-Native($what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { Fail "$what (exit code $LASTEXITCODE)" }
}

# ---- version & paths -------------------------------------------------------------------------------------------
$props = [xml](Get-Content "$Root\Directory.Build.props" -Raw)
$Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $Version) { Write-Error 'No <Version> in Directory.Build.props'; exit 1 }

$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Dist = Join-Path $Root 'dist'
$Work = Join-Path $Dist "_build\$Version-$Stamp"
$Portable = Join-Path $Work 'portable'
$Installer = Join-Path $Work 'installer'
# scratch output of earlier failed builds is disposable; releases and dist/portable are never touched here
if (Test-Path (Join-Path $Dist '_build')) { Remove-Item (Join-Path $Dist '_build') -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $Work, $Portable | Out-Null

$commit = (git rev-parse --short HEAD 2>$null); if (-not $commit) { $commit = 'unknown' }
$dirty = [bool](git status --porcelain 2>$null | Select-Object -First 1)

Write-Host "NetPulse build  version $Version  commit $commit$(if ($dirty) { ' (+uncommitted changes)' })" -ForegroundColor Cyan

# ---- 1. build --------------------------------------------------------------------------------------------------
Step '1/6 Release build (warnings are errors)'
Run-Native 'dotnet build' { dotnet build "$Root\NetPulse.slnx" -c Release -nologo -p:TreatWarningsAsErrors=true -v:q }
Ok 'Build succeeded with 0 warnings'

# ---- 2. tests --------------------------------------------------------------------------------------------------
Step '2/6 Automated tests'
$trxDir = Join-Path $Work 'tests'
Run-Native 'dotnet test' { dotnet test "$Root\NetPulse.slnx" -c Release --no-build -nologo --logger "trx;LogFileName=results.trx" --results-directory $trxDir -v:q }
$trx = [xml](Get-Content (Join-Path $trxDir 'results.trx') -Raw)
$c = $trx.TestRun.ResultSummary.Counters
if ([int]$c.failed -ne 0 -or [int]$c.total -eq 0) { Fail "tests: $($c.failed) failed of $($c.total)" }
$Tests = @{ total = [int]$c.total; passed = [int]$c.passed; failed = [int]$c.failed }
Ok "$($Tests.passed)/$($Tests.total) tests passed"

# ---- 3. publish portable ---------------------------------------------------------------------------------------
Step '3/6 Publish portable build (win-x64, self-contained, single file)'
# The exe carries its own .NET runtime and Windows Update never patches it: bundle the latest patch, with its security fixes.
if (-not $RuntimeVersion) {
    try {
        $feed = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' -TimeoutSec 20
        $RuntimeVersion = $feed.'latest-runtime'
    }
    catch { Fail "could not read the latest .NET runtime from Microsoft's release feed ($($_.Exception.Message)); pass -RuntimeVersion 10.0.x" }
}
if ($RuntimeVersion -notmatch '^10\.0\.\d+$') { Fail "unexpected .NET runtime version '$RuntimeVersion'" }
Ok ".NET runtime to bundle: $RuntimeVersion"
Run-Native 'dotnet publish (app)' {
    dotnet publish "$Root\src\NetPulse.App\NetPulse.App.csproj" -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:NetPulseRuntimeVersion=$RuntimeVersion -nologo -v:q -o $Portable
}
Copy-Item "$Root\src\NetPulse.App\app.ico" "$Portable\app.ico" -Force
$exe = Join-Path $Portable 'NetPulse.exe'
if (-not (Test-Path $exe)) { Fail 'NetPulse.exe was not produced' }
$exeVersion = (Get-Item $exe).VersionInfo.ProductVersion
if (-not $exeVersion -or -not $exeVersion.StartsWith($Version)) { Fail "exe version '$exeVersion' does not match $Version" }
Ok ("{0}  ({1:N1} MB, product version {2})" -f $exe, ((Get-Item $exe).Length / 1MB), $exeVersion)

# ---- 4. verify the PUBLISHED exe -------------------------------------------------------------------------------
Step '4/6 Verify the published build'
$before = Get-ChildItem $Portable -Recurse | ForEach-Object { "$($_.FullName)|$($_.Length)" } | Sort-Object

$selfJson = Join-Path $Work 'selftest.json'
$selfData = Join-Path $Work 'selftest-data'
$p = Start-Process -FilePath $exe -ArgumentList '--selftest', '--selftest-out', "`"$selfJson`"", '--selftest-data', "`"$selfData`"" -PassThru
if (-not $p.WaitForExit(120000)) { $p.Kill(); Fail 'self-test timed out' }
if (-not (Test-Path $selfJson)) { Fail 'self-test produced no report' }
$self = Get-Content $selfJson -Raw | ConvertFrom-Json
foreach ($chk in $self.Checks) { Write-Host ("   [{0}] {1}" -f $(if ($chk.Passed) { 'PASS' } else { 'FAIL' }), $chk.Name) -ForegroundColor $(if ($chk.Passed) { 'Green' } else { 'Red' }) }
if ($p.ExitCode -ne 0 -or -not $self.Ok) { Fail 'published self-test failed' }
Ok "self-test passed ($($self.Checks.Count) checks)"
if ($self.Runtime -ne $RuntimeVersion) { Fail "the published exe runs on .NET $($self.Runtime), not $RuntimeVersion" }
Ok "the published exe runs on .NET $($self.Runtime)"

$GuiResult = 'skipped'
if (-not $SkipGuiCheck) {
    $guiData = Join-Path $Work 'gui-data'
    $guiJson = Join-Path $Work 'gui.json'
    $standIn = "$Root\tests\NetPulse.FakeGame\bin\Release\net10.0\StandInGame.exe"
    if (-not (Test-Path $standIn)) { Fail "the stand-in game for the GUI check was not built ($standIn)" }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$Root\scripts\Verify-Gui.ps1" -Exe $exe -DataDir $guiData -Out $guiJson -FakeGame $standIn
    if ($LASTEXITCODE -ne 0) { Fail 'GUI verification of the published build failed' }
    $GuiResult = 'passed'
    Ok 'Game Monitor workflow verified in the published exe'
}
else {
    Write-Host '   GUI check skipped (-SkipGuiCheck)' -ForegroundColor Yellow
}

$after = Get-ChildItem $Portable -Recurse | ForEach-Object { "$($_.FullName)|$($_.Length)" } | Sort-Object
if (Compare-Object $before $after) { Fail 'running the published exe wrote files next to it (user data must live in %LOCALAPPDATA%\NetPulse)' }
Ok 'Nothing was written next to the executable'

# ---- 5. installer ----------------------------------------------------------------------------------------------
$InstallerResult = 'not built'
if (-not $SkipInstaller) {
    Step '5/6 Installer (NetPulse-Setup.exe)'
    New-Item -ItemType Directory -Force -Path "$Installer\payload" | Out-Null
    Run-Native 'dotnet publish (installer)' {
        dotnet publish "$Root\src\NetPulse.Installer\NetPulse.Installer.csproj" -c Release -r win-x64 --self-contained `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:NetPulseRuntimeVersion=$RuntimeVersion -nologo -v:q -o $Installer
    }
    Copy-Item $exe "$Installer\payload\NetPulse.exe" -Force
    Copy-Item "$Portable\app.ico" "$Installer\payload\app.ico" -Force
    Copy-Item "$Root\installer\Setup-NetPulse.ps1" "$Installer\Setup-NetPulse.ps1" -Force
    if (-not (Test-Path "$Installer\NetPulse-Setup.exe")) { Fail 'NetPulse-Setup.exe was not produced' }
    $InstallerResult = 'built (not install-tested)'
    Ok 'NetPulse-Setup.exe built'

    if ($VerifyInstaller) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$Root\scripts\Verify-Installer.ps1" -Setup "$Installer\NetPulse-Setup.exe" -WorkDir (Join-Path $Work 'installer-test')
        if ($LASTEXITCODE -ne 0) { Fail 'installer verification failed' }
        $InstallerResult = 'built; install and uninstall verified'
        Ok 'Installer install/uninstall verified'
    }
}
else {
    Step '5/6 Installer skipped (-SkipInstaller)'
}

# ---- 6. promote ------------------------------------------------------------------------------------------------
Step '6/6 Promote verified build'
$notesText = Get-Content "$Root\RELEASE_NOTES.md" -Raw
$section = [regex]::Match($notesText, "(?ms)^## $([regex]::Escape($Version))\s*\r?\n(.*?)(?=^## |\z)")
if (-not $section.Success) { Fail "RELEASE_NOTES.md has no '## $Version' section; add concise release notes first" }

$Releases = Join-Path $Dist 'releases'
New-Item -ItemType Directory -Force -Path $Releases | Out-Null
$releaseName = $Version
$n = 2
while (Test-Path (Join-Path $Releases $releaseName)) { $releaseName = "$Version-build$n"; $n++ }   # never overwrite an earlier release
$Release = Join-Path $Releases $releaseName
$Pending = Join-Path $Releases "_pending-$Stamp"   # assembled here and renamed only when complete, so a failure never leaves a half-written release
$zipName = "NetPulse-$releaseName-Portable-win-x64.zip"

try {
    New-Item -ItemType Directory -Force -Path $Pending | Out-Null
    New-Zip $Portable (Join-Path $Pending $zipName)
    if (Test-Path "$Installer\NetPulse-Setup.exe") { Copy-Item $Installer (Join-Path $Pending 'installer') -Recurse -Force }

    "# NetPulse $Version`n`nBuilt $((Get-Date).ToString('u')) from commit $commit$(if ($dirty) { ' (with uncommitted changes)' }).`n`n$($section.Groups[1].Value.Trim())`n" |
        Set-Content (Join-Path $Pending 'RELEASE_NOTES.md') -Encoding UTF8

    [pscustomobject]@{
        version        = $Version
        release        = $releaseName
        builtUtc       = (Get-Date).ToUniversalTime().ToString('o')
        commit         = $commit
        uncommittedChanges = $dirty
        dotnetRuntime  = $RuntimeVersion
        tests          = $Tests
        publishedSelfTest = "passed ($($self.Checks.Count) checks)"
        publishedGuiCheck = $GuiResult
        installer      = $InstallerResult
        exeSha256      = Get-Sha256 $exe
        zipSha256      = Get-Sha256 (Join-Path $Pending $zipName)
        dataLocation   = '%LOCALAPPDATA%\NetPulse (settings, sessions); nothing is stored beside the executable'
    } | ConvertTo-Json | Set-Content (Join-Path $Pending 'build-info.json') -Encoding UTF8
    Rename-Item $Pending $releaseName   # the release becomes visible only now

    # swap dist/portable only now; keep the previous one until the new one is in place
    $PortableDir = Join-Path $Dist 'portable'
    $PortableNew = Join-Path $Dist 'portable.new'
    $PortablePrev = Join-Path $Dist 'portable.prev'
    foreach ($d in $PortableNew, $PortablePrev) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
    Copy-Item $Portable $PortableNew -Recurse
    if (Test-Path $PortableDir) { Rename-Item $PortableDir 'portable.prev' }
    try { Rename-Item $PortableNew 'portable' }
    catch { if (Test-Path $PortablePrev) { Rename-Item $PortablePrev 'portable' }; throw }
    if (Test-Path $PortablePrev) { Remove-Item $PortablePrev -Recurse -Force }

    Copy-Item (Join-Path $Release $zipName) (Join-Path $Dist "NetPulse-$Version-Portable-win-x64.zip") -Force
    "$releaseName`n$Release" | Set-Content (Join-Path $Dist 'LATEST.txt')
}
catch {
    if (Test-Path $Pending) { Remove-Item $Pending -Recurse -Force -ErrorAction SilentlyContinue }
    Fail "promotion failed: $($_.Exception.Message)"
}

Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
$buildDir = Join-Path $Dist '_build'
if ((Test-Path $buildDir) -and -not (Get-ChildItem $buildDir)) { Remove-Item $buildDir -Force }

Write-Host "`n=====================================================" -ForegroundColor Green
Write-Host " NetPulse $Version verified and packaged" -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green
Write-Host " Portable exe : $(Join-Path $Dist 'portable\NetPulse.exe')"
Write-Host " Portable zip : $(Join-Path $Dist "NetPulse-$Version-Portable-win-x64.zip")"
Write-Host " Release      : $Release"
Write-Host " Installer    : $InstallerResult"
Write-Host " Tests        : $($Tests.passed)/$($Tests.total)   Published self-test: passed   GUI check: $GuiResult"
Write-Host " .NET runtime : $RuntimeVersion (bundled)"
exit 0
