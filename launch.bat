@echo off
rem Automata redeploy convenience launcher.
rem Shuts down any running instance, clears build cache, rebuilds, publishes to
rem C:\Apps\Automata\, and launches that deployed copy. Same process KdpPublish
rem (tools\deploy.ps1) and IdiotProof (tools\publish-all.ps1) use to always run a
rem fresh, independent deployed copy instead of a possibly-stale one.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\deploy.ps1" -Launch %*
