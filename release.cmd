@echo off
rem Double-click to release a new version to GitHub Releases (asks for the version, checks git, tags and pushes).
rem Options are passed through to tools\release.ps1 (e.g. release.cmd 0.2.0 -DryRun, release.cmd 0.2.0 -Local).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" %*
