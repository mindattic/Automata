#Requires -Version 5.1
<#
.SYNOPSIS
    Shuts down any running Automata, clears build cache, rebuilds, and publishes a
    standalone copy to C:\Apps\Automata\ — same process KdpPublish (tools/deploy.ps1)
    and IdiotProof (tools/publish-all.ps1) use to always have a fresh, independent
    deployed copy.

.DESCRIPTION
    1. Stops any running Automata.App.exe process.
    2. Removes bin/obj for the App + Core + Tests projects — a clean, timestamp-
       independent rebuild, not an incremental one. This is the BUILD cache only; the
       WebView2 user-data folders under %LocalAppData%\MindAttic\Automata\ (which hold
       target-site login sessions) are never touched, so logins survive redeploys.
    3. dotnet publish (Release, win-x64, framework-dependent single file) -> C:\Apps\Automata\.
       wwwroot (panel.html/panel.js/app.css/target\recorder.js) publishes as loose files
       beside the exe — the app reads them from disk at runtime.
    4. Writes C:\Apps\Automata\launch.bat, which calls this same deploy.ps1 (by its
       baked-in source path) EVERY time before launching, so a double-click always runs
       the current source rather than risking a stale deployed copy. If the source repo
       is missing (moved/deleted), it warns and launches whatever is already deployed.

.PARAMETER Launch
    After publishing, immediately run C:\Apps\Automata\launch.bat's exe.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\deploy.ps1
    powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -Launch
#>
param([switch]$Launch)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoDir = Split-Path $PSScriptRoot   # tools\ -> Automata\
$proj    = Join-Path $repoDir 'Automata.App\Automata.App.csproj'
$out     = 'C:\Apps\Automata'
$exeName = 'Automata.App.exe'

# ── Stop running instance ──────────────────────────────────────────────────
Write-Host ''
Write-Host '  Stopping running instance...' -ForegroundColor Yellow
$procs = Get-Process 'Automata.App' -ErrorAction SilentlyContinue
if ($procs) {
    $procs | Stop-Process -Force
    Write-Host "    Stopped ($(@($procs).Count) process(es))" -ForegroundColor DarkYellow
    Start-Sleep -Milliseconds 800
} else {
    Write-Host '    Nothing running.' -ForegroundColor DarkGray
}

# ── Clear build cache ──────────────────────────────────────────────────────
# bin/obj only — never the WebView2 user-data folders under
# %LocalAppData%\MindAttic\Automata\, which hold target-site logins and must survive
# a redeploy exactly as they survive a normal rebuild.
Write-Host ''
Write-Host '  Clearing build cache (bin/obj)...' -ForegroundColor Cyan
foreach ($rel in @('Automata.App', 'Automata.Core', 'Automata.Tests')) {
    $projRoot = Join-Path $repoDir $rel
    if (-not (Test-Path $projRoot)) { continue }
    foreach ($d in @((Join-Path $projRoot 'bin'), (Join-Path $projRoot 'obj'))) {
        if (Test-Path $d) {
            try { Remove-Item -Recurse -Force $d -ErrorAction Stop }
            catch { Write-Host "    (could not fully remove $d : $($_.Exception.Message))" -ForegroundColor DarkYellow }
        }
    }
}

# ── Publish ─────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host "  Publishing $proj" -ForegroundColor Cyan
Write-Host "    -> $out\$exeName"

dotnet publish $proj `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    --output $out | Out-Host

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $out $exeName
if (-not (Test-Path $exe)) {
    Write-Host "Publish completed but $exeName not found at $exe." -ForegroundColor Red
    exit 1
}

# ── Write self-redeploying launcher ─────────────────────────────────────────
# Always redeploys from source before launching, so a double-click can never run a
# stale build. $PSScriptRoot here is THIS script's own folder — baked into the batch
# file as an absolute path so it still resolves no matter where launch.bat itself is
# invoked from. Falls back to launching the existing exe (with a warning) if that path
# is missing, rather than refusing to start.
$deployPs1Path = Join-Path $PSScriptRoot 'deploy.ps1'
$launchBatPath = Join-Path $out 'launch.bat'
@(
    '@echo off',
    'title Automata',
    "set `"DEPLOY_PS1=$deployPs1Path`"",
    'if exist "%DEPLOY_PS1%" (',
    '    echo Redeploying latest build from source...',
    '    powershell -NoProfile -ExecutionPolicy Bypass -File "%DEPLOY_PS1%"',
    '    if errorlevel 1 echo Redeploy failed - launching whatever is already in this folder instead.',
    ') else (',
    '    echo Source repo not found at "%DEPLOY_PS1%" - launching existing build without redeploying.',
    ')',
    "start `"`" `"%~dp0$exeName`""
) | Set-Content $launchBatPath -Encoding ascii

# ── Summary ────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '  Published successfully.' -ForegroundColor Green
Write-Host "    Exe    : $exe ($([math]::Round(((Get-Item $exe).Length / 1MB), 1)) MB)" -ForegroundColor Gray
Write-Host "    Launch : $launchBatPath" -ForegroundColor Gray
Write-Host ''

if ($Launch) {
    Write-Host '  Launching...' -ForegroundColor Cyan
    Start-Process $exe
}
