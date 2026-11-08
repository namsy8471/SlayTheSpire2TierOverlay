# Regenerates data/local (card art, localization, card catalog, manifest.json) from the installed game.
# The overlay does this by itself on start; use this script to do it ahead of time or to force a rebuild.
#
#   pwsh tools/refresh-local.ps1                 # only if the game changed (manifest.json fingerprint)
#   pwsh tools/refresh-local.ps1 -Force          # always rebuild
#   pwsh tools/refresh-local.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Slay the Spire 2"
#
# Exit code: 0 ok / already up to date, 1 failed, 2 game not found.
param(
    [switch]$Force,
    [string]$GameDir
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$appArgs = @('--prepare')
if ($Force) { $appArgs += '--force' }
if ($GameDir) { $appArgs += @('--game-dir', $GameDir) }

$project = Join-Path $repo 'src/Sts2Overlay.App'
dotnet build $project -c Release -v quiet -nologo
if ($LASTEXITCODE -ne 0) { exit 1 }

# The app is a WinExe (no console of its own). Piping its output makes PowerShell hand it stdout and wait for it,
# so the progress lines show up here and $LASTEXITCODE is the app's exit code.
$exe = Join-Path $project 'bin/Release/net10.0-windows/Sts2Overlay.exe'
& $exe @appArgs | Out-Host
exit $LASTEXITCODE
