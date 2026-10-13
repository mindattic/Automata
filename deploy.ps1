#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and deploys Automata to C:\Apps\Automata\ and optionally runs it. The one
    script for publishing and launching Automata.

.DESCRIPTION
    Without -Force it only republishes when the deployed exe or deploy.bat is missing,
    or a source file is newer than the deployed exe, so the per-launch call from
    deploy.bat is a fast no-op. When it does publish:
    1. Stops any running Automata.App.exe process.
    2. With -Force only: removes bin/obj for the App + Core + Tests projects (a clean
       rebuild). This is the BUILD cache only; the WebView2 user-data folders under
       %LocalAppData%\MindAttic\Automata\ (which hold target-site login sessions) are
       never touched, so logins survive redeploys.
    3. dotnet publish (Release, win-x64, framework-dependent single file) -> C:\Apps\Automata\.
       wwwroot (panel.html/panel.js/app.css/target\recorder.js) publishes as loose files
       beside the exe; the app reads them from disk at runtime.
    4. Writes C:\Apps\Automata\deploy.bat (the desktop shortcut target), which calls this
       deploy.ps1 (by its baked-in source path) before every launch, then starts the exe
       with its arguments passed through. If the source repo is missing (moved/deleted),
       it warns and launches whatever is already deployed.

.PARAMETER Force
    Republish with a clean bin/obj rebuild even if nothing looks changed.

.PARAMETER Launch
    After deploying, start the deployed exe with any remaining arguments.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy.ps1
    powershell -ExecutionPolicy Bypass -File deploy.ps1 -Force
    powershell -ExecutionPolicy Bypass -File deploy.ps1 -Launch
#>
param(
    [switch]$Force,
    [switch]$Launch,
    [Parameter(ValueFromRemainingArguments)]
    [string[]]$LaunchArgs = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoDir = $PSScriptRoot
$proj    = Join-Path $repoDir 'Automata.App\Automata.App.csproj'
$out     = 'C:\Apps\Automata'
$exeName = 'Automata.App.exe'
$exe     = Join-Path $out $exeName
$bat     = Join-Path $out 'deploy.bat'

# .js/.html/.css: wwwroot (the panel) publishes as loose files, so edits there need a redeploy too.
$sourceExtensions = @('.cs', '.csproj', '.xaml', '.razor', '.json', '.props', '.targets', '.resx',
                      '.js', '.html', '.css')
$skipDirs         = @('bin', 'obj', '.git', 'node_modules')

function Test-NeedsPublish {
    if (-not (Test-Path $exe)) { return $true }
    if (-not (Test-Path $bat)) { return $true }
    $exeTime = (Get-Item $exe).LastWriteTimeUtc
    foreach ($p in 'Directory.Build.props', 'Directory.Packages.props') {
        $f = Join-Path $repoDir $p
        if ((Test-Path $f) -and (Get-Item $f).LastWriteTimeUtc -gt $exeTime) { return $true }
    }
    # Walk the repo, pruning build output, dot-folders and node_modules rather than enumerating
    # them, so the no-change path stays fast.
    $stack = New-Object System.Collections.Stack
    $stack.Push((Get-Item $repoDir))
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        foreach ($item in (Get-ChildItem -LiteralPath $dir.FullName -Force -ErrorAction SilentlyContinue)) {
            if ($item.PSIsContainer) {
                # Dot-folders (.git, .vs, .claude, .prose) hold tooling state, not source;
                # agent session .json files there change constantly.
                if (-not $item.Name.StartsWith('.') -and
                    $skipDirs -notcontains $item.Name.ToLowerInvariant()) { $stack.Push($item) }
            } elseif ($sourceExtensions -contains $item.Extension.ToLowerInvariant() -and
                      $item.LastWriteTimeUtc -gt $exeTime) {
                return $true
            }
        }
    }
    return $false
}

if (-not $Force -and -not (Test-NeedsPublish)) {
    Write-Host '  Automata is up to date - nothing to deploy.' -ForegroundColor DarkGray
} else {
    # ── Stop running instance ──────────────────────────────────────────────
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

    # ── Clear build cache (-Force only) ────────────────────────────────────
    # bin/obj only — never the WebView2 user-data folders under
    # %LocalAppData%\MindAttic\Automata\, which hold target-site logins and must survive
    # a redeploy exactly as they survive a normal rebuild.
    if ($Force) {
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
    }

    # ── Publish ────────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host "  Publishing $proj" -ForegroundColor Cyan
    Write-Host "    -> $exe"

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
    if (-not (Test-Path $exe)) {
        Write-Host "Publish completed but $exeName not found at $exe." -ForegroundColor Red
        exit 1
    }

    # ── Write self-redeploying launcher ────────────────────────────────────
    # The repo path is baked in as an absolute path so deploy.bat resolves this script
    # wherever it is started from. Falls back to launching the existing exe (with a
    # warning) if that path is missing, rather than refusing to start.
    $deployBat = @"
@echo off
title Automata
rem Generated by $repoDir\deploy.ps1 - edits here are overwritten.
set "DEPLOY_PS1=$repoDir\deploy.ps1"
if exist "%DEPLOY_PS1%" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%DEPLOY_PS1%"
    if errorlevel 1 echo Redeploy failed - launching whatever is already in this folder instead.
) else (
    echo Source repo not found at "%DEPLOY_PS1%" - launching existing build without redeploying.
)
start "" "%~dp0$exeName" %*
"@
    Set-Content -LiteralPath $bat -Value $deployBat -Encoding ascii

    # The old generated launcher is replaced by deploy.bat.
    $oldBat = Join-Path $out 'launch.bat'
    if (Test-Path $oldBat) { Remove-Item -LiteralPath $oldBat -Force }

    # ── Summary ────────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host '  Published successfully.' -ForegroundColor Green
    Write-Host "    Exe    : $exe ($([math]::Round(((Get-Item $exe).Length / 1MB), 1)) MB)" -ForegroundColor Gray
    Write-Host "    Launch : $bat" -ForegroundColor Gray
    Write-Host ''
}

if ($Launch) {
    if (-not (Test-Path $exe)) {
        Write-Host "Nothing deployed at $exe." -ForegroundColor Red
        exit 1
    }
    Write-Host '  Launching...' -ForegroundColor Cyan
    if ($LaunchArgs.Count -gt 0) { Start-Process $exe -ArgumentList $LaunchArgs }
    else { Start-Process $exe }
}
exit 0
