@echo off
rem Double-click to release a new version of the Rust overlay to GitHub Releases (builds on this PC, then uploads).
rem Options are passed through to tools\release-rust.ps1 (e.g. release.cmd 0.2.0 -AdvisorZip artifacts\advisor-0.2.0.zip -DryRun).
rem The old C# flow is tools\release.ps1 (no longer used).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release-rust.ps1" %*
