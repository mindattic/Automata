#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and deploys Automata to C:\Apps\Automata\ and optionally runs it. The one
    script for publishing and launching Automata.

.DESCRIPTION
    Never prompts: agents and /deploy run it non-interactively. The interactive choice lives
    in the generated launcher C:\Apps\Automata\run.bat (the desktop shortcut target),
    which, started with no arguments (a desktop double-click), opens a menu first:
      1) Deploy & Run - calls this script with -Force (always republishes), then starts the exe.
      2) Run          - starts the already-deployed exe without calling this script at all
                        (falls through to Deploy & Run if nothing is deployed yet).
    Started with arguments (another tool calling it) it never prompts: it runs this script
    without -Force (republish only if changed), then starts the exe with those arguments.
    Either way the exe gets the launcher's arguments passed through. If the source repo is
    missing (moved/deleted), Deploy & Run warns and launches whatever is already deployed;
    if the redeploy fails, it launches the installed copy too.

    Every run (published or not) rewrites that launcher when its text has changed, so an
    existing install picks up launcher changes without a republish.

    Without -Force it only republishes when the deployed exe is missing, or a source file
    is newer than the deployed exe. When it does publish:
    1. Stops any running Automata.App.exe process.
    2. With -Force only: removes bin/obj for the App + Core + Tests projects (a clean
       rebuild). This is the BUILD cache only; the WebView2 user-data folders under
       %LocalAppData%\MindAttic\Automata\ (which hold target-site login sessions) are
       never touched, so logins survive redeploys.
    3. dotnet publish (Release, win-x64, framework-dependent single file) -> C:\Apps\Automata\.
       wwwroot (panel.html/panel.js/app.css/target\recorder.js) publishes as loose files
       beside the exe; the app reads them from disk at runtime.

.PARAMETER Force
    Republish with a clean bin/obj rebuild even if nothing looks changed.

.PARAMETER LauncherOnly
    Only (re)write the generated launcher and exit: no build, nothing stopped.

.PARAMETER Launch
    After deploying, start the deployed exe with any remaining arguments.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File run.ps1
    powershell -ExecutionPolicy Bypass -File run.ps1 -Force
    powershell -ExecutionPolicy Bypass -File run.ps1 -Launch
    powershell -ExecutionPolicy Bypass -File run.ps1 -LauncherOnly
#>
param(
    [switch]$Force,
    [switch]$LauncherOnly,
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
$bat     = Join-Path $out 'run.bat'

# .js/.html/.css: wwwroot (the panel) publishes as loose files, so edits there need a redeploy too.
$sourceExtensions = @('.cs', '.csproj', '.xaml', '.razor', '.json', '.props', '.targets', '.resx',
                      '.js', '.html', '.css')
$skipDirs         = @('bin', 'obj', '.git', 'node_modules')

function Test-NeedsPublish {
    if (-not (Test-Path $exe)) { return $true }
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

# Writes the double-click launcher (only when its text changed, so a launcher that is running
# this script is not rewritten under itself) and removes the stale launchers it replaced.
# The repo path is baked in as an absolute path so the launcher finds this script wherever
# it is started from. The menu lives here, not in this script, so the script never blocks.
function Write-Launcher {
    $text = @"
@echo off
title Automata
rem Generated by $repoDir\run.ps1 - edits here are overwritten.
set "RUN_PS1=$repoDir\run.ps1"
rem Started with arguments (another tool calling it): no menu, never prompt.
set "ARG1=%~1"
if defined ARG1 goto auto
echo.
echo   Automata
echo     1^) Deploy ^& Run
echo     2^) Run
echo.
choice /c 12 /n /m "  Choose 1 or 2: "
if errorlevel 2 goto run
:deploy
if exist "%RUN_PS1%" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%RUN_PS1%" -Force
    if errorlevel 1 echo Redeploy failed - launching whatever is already in this folder instead.
) else (
    echo Source repo not found at "%RUN_PS1%" - launching existing build without redeploying.
)
goto launch
:auto
if exist "%RUN_PS1%" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%RUN_PS1%"
    if errorlevel 1 echo Redeploy failed - launching whatever is already in this folder instead.
) else (
    echo Source repo not found at "%RUN_PS1%" - launching existing build without redeploying.
)
goto launch
:run
if exist "%~dp0$exeName" goto launch
echo Nothing is deployed yet - deploying first.
goto deploy
:launch
if not exist "%~dp0$exeName" (
    echo Nothing deployed at "%~dp0$exeName".
    if not defined ARG1 pause
    exit /b 1
)
start "" "%~dp0$exeName" %*
"@
    # CRLF throughout: cmd mis-finds labels (goto) in LF-only batch files.
    $text = ($text -replace "`r?`n", "`r`n") + "`r`n"
    if (-not (Test-Path $out)) { New-Item -ItemType Directory -Force -Path $out | Out-Null }
    $current = if (Test-Path $bat) { [IO.File]::ReadAllText($bat) } else { $null }
    if ($current -ne $text) { [IO.File]::WriteAllText($bat, $text, [Text.Encoding]::ASCII) }

    # Stale launchers this one replaced: launch.bat, then deploy.bat (renamed to run.bat).
    foreach ($old in 'launch.bat', 'deploy.bat') {
        $oldBat = Join-Path $out $old
        if (Test-Path $oldBat) { Remove-Item -LiteralPath $oldBat -Force }
    }
}

if ($LauncherOnly) {
    Write-Launcher
    Write-Host "  Wrote $bat" -ForegroundColor Gray
    exit 0
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

    # ── MindAttic Vault settings (LAMP, AUT-T27) ───────────────────────────
    # Imports today's settings from the database into %APPDATA%\MindAttic\Automata\settings.json (once)
    # and writes settings.manifest.json beside it, so MindAttic.Vault.Desktop shows Automata's tab.
    Write-Host ''
    Write-Host '  Writing the MindAttic Vault settings manifest...' -ForegroundColor Cyan
    $manifest = Start-Process -FilePath $exe -ArgumentList '--write-settings-manifest' -Wait -PassThru
    if ($manifest.ExitCode -ne 0) {
        Write-Host "    (--write-settings-manifest exited $($manifest.ExitCode); Automata writes it on its next start)" -ForegroundColor DarkYellow
    }

    # ── Summary ────────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host '  Published successfully.' -ForegroundColor Green
    Write-Host "    Exe    : $exe ($([math]::Round(((Get-Item $exe).Length / 1MB), 1)) MB)" -ForegroundColor Gray
    Write-Host "    Launch : $bat" -ForegroundColor Gray
    Write-Host ''
}

Write-Launcher

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
