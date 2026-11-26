#Requires -Version 5.1
<#
.SYNOPSIS
    This repo's run menu: 1) Redeploy  2) Run Debug  3) Run Release  (the MindAttic standard, MAT-T279).

.DESCRIPTION
    Copied from MindAttic.Agency\shared\templates\run.ps1 (Write-RunPs1 in deploy-lib.ps1). run.bat beside it
    (Write-RunBat) opens the same menu on a double-click. Everything else lives in deploy-lib.ps1's Invoke-AppRun:
      1) Redeploy     build and deploy Debug and Release to R:\MindAttic\Apps\<repo>\{Debug,Release}
      2) Run Debug    the dev/test copy, in this console; extra arguments pass through to the app
      3) Run Release  the standalone copy, started the way a double-click would start it
    Picks combine: 13 is Deploy & Run (redeploy, then run the installed Release copy).

    Finding deploy-lib.ps1, first match wins:
      1. $env:MINDATTIC_DEPLOY_LIB, the full path of a deploy-lib.ps1 (a dev copy, or a workspace elsewhere)
      2. walking up from this folder, at each ancestor <dir>:
           <dir>\MindAttic.Agency\shared\deploy-lib.ps1           (the canonical copy, current with main)
           <dir>\mindattic-agent-standard\shared\deploy-lib.ps1   (the workspace-root mirror; lags until
                                                                    sync-workspace-copy.ps1 runs)
      3. the same two under D:\Projects\MindAttic (repos in another workspace root, e.g. D:\Projects\GunGreenEyes)

    The repo's own differences go in the override block below (or in .agent\run.json; this block's keys win).

.EXAMPLE
    .\run.ps1                    the menu
    .\run.ps1 -Redeploy -RunDebug --level 3     (a pass-through spelled like a common parameter, e.g. --verbose,
                                                 is taken by PowerShell itself; put it in DebugArgs instead)
    .\run.ps1 -RunRelease                       Run: the installed copy, no build
    .\run.ps1 -Redeploy -RunRelease             Deploy & Run
    .\run.ps1 -Redeploy -DryRun  print what would be built and launched, do nothing
#>
param(
    [switch]$Redeploy,
    [switch]$RunDebug,
    [switch]$RunRelease,
    [switch]$DryRun,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArgs = @()
)
$ErrorActionPreference = 'Stop'

# The override block: every key is optional; delete what you don't need. See deploy-lib.ps1's header for each key.
#
# Automata deploys two exes into each folder: the desktop app (Automata.App.exe, the one the menu runs) and the
# headless runner (automata-runner.exe). wwwroot (the panel) publishes as loose files beside the app exe.
# PostBuild runs Automata.App.exe --write-settings-manifest, which imports today's settings into
# %APPDATA%\MindAttic\Automata\settings.json (once) and writes the manifest MindAttic.Vault.Desktop reads (AUT-T27).
# A redeploy never touches the WebView2 profiles under %LocalAppData%\MindAttic\Automata\, so site logins survive.
$Override = @{
    Exe       = 'Automata.App.exe'
    Plan      = @{ Kind = 'dotnet'; Projects = @(
            "$PSScriptRoot\Automata.App\Automata.App.csproj",
            "$PSScriptRoot\Automata.Runner\Automata.Runner.csproj") }
    PostBuild = {
        param($Config, $Out)
        $p = Start-Process -FilePath (Join-Path $Out 'Automata.App.exe') -ArgumentList '--write-settings-manifest' -Wait -PassThru
        if ($p.ExitCode -ne 0) { Write-Host "  (--write-settings-manifest exited $($p.ExitCode); Automata writes it on its next start)" -ForegroundColor DarkYellow }
    }
}

function Find-DeployLib {
    if ($env:MINDATTIC_DEPLOY_LIB -and (Test-Path -LiteralPath $env:MINDATTIC_DEPLOY_LIB)) { return $env:MINDATTIC_DEPLOY_LIB }
    $roots = @()
    $d = $PSScriptRoot
    while ($d) { $roots += $d; $d = Split-Path -Parent $d }
    $roots += 'D:\Projects\MindAttic'
    foreach ($r in $roots) {
        foreach ($rel in 'MindAttic.Agency\shared\deploy-lib.ps1', 'mindattic-agent-standard\shared\deploy-lib.ps1') {
            $p = Join-Path $r $rel
            if (Test-Path -LiteralPath $p) { return $p }
        }
    }
    throw 'deploy-lib.ps1 not found: clone MindAttic.Agency beside this repo, or set MINDATTIC_DEPLOY_LIB to its path.'
}

. (Find-DeployLib)
Invoke-AppRun -Repo $PSScriptRoot -Redeploy:$Redeploy -RunDebug:$RunDebug -RunRelease:$RunRelease -DryRun:$DryRun `
    -AppArgs $AppArgs -Override $Override
