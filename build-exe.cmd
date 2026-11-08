@echo off
rem Double-click to pull the latest code (git pull --ff-only) and rebuild dist\Sts2Overlay.exe.
rem Options are passed through to tools\build-exe.ps1 (e.g. build-exe.cmd -NoPull to skip the pull).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\build-exe.ps1" %*
