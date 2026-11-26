@echo off
rem The MindAttic standard run.bat (MAT-T279), written by deploy-lib.ps1's Write-RunBat - edits here are overwritten.
rem Double-click: the run.ps1 menu (1 Redeploy, 2 Run Debug, 3 Run Release). Arguments pass through to run.ps1.
title Automata
set "RUN_PS1=%~dp0run.ps1"
if not exist "%RUN_PS1%" (
    echo run.ps1 not found at "%RUN_PS1%".
    pause
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%RUN_PS1%" %*
if errorlevel 1 (
    echo.
    echo run.ps1 failed ^(exit %errorlevel%^).
    pause
    exit /b 1
)
exit /b 0
